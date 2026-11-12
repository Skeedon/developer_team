using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DeveloperTeam.Data;
using DeveloperTeam.Domain;
using DeveloperTeam.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class ImagePipelineTests
{
    private sealed class FakeImageApi(params bool[] reviewPasses) : HttpMessageHandler
    {
        public string ExpectedModel { get; init; } = "gpt-image-1.5";
        public int DraftCalls { get; private set; }
        public int EditCalls { get; private set; }
        public int ReviewCalls { get; private set; }
        public readonly List<string> Endpoints = [];
        private readonly byte[] image = [137, 80, 78, 71, 13, 10, 26, 10, .. Enumerable.Repeat((byte)4, 160)];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Endpoints.Add(path);
            if (req.Headers.Authorization?.Scheme != "Bearer")
                throw new InvalidOperationException("Authorization missing.");
            var body = await req.Content!.ReadAsStringAsync(ct);
            if (path.EndsWith("/images/generations"))
            {
                DraftCalls++;
                Assert.Contains("\"quality\":\"low\"", body);
                Assert.Contains("\"model\":\"" + ExpectedModel + "\"", body);
                Assert.Contains("\"background\":\"transparent\"", body);
                return Json(new { data = new[] { new { b64_json = Convert.ToBase64String(image) } },
                    usage = new { input_tokens = 123, output_tokens = 456 } });
            }
            if (path.EndsWith("/images/edits"))
            {
                EditCalls++;
                Assert.Contains("\"quality\":\"high\"", body);
                Assert.Contains("\"model\":\"" + ExpectedModel + "\"", body);
                Assert.Contains("\"background\":\"transparent\"", body);
                Assert.Contains("data:image/png;base64,", body);
                return Json(new { data = new[] { new { b64_json = Convert.ToBase64String(image) } },
                    usage = new { input_tokens = 456, output_tokens = 789 } });
            }
            if (path.EndsWith("/responses"))
            {
                ReviewCalls++;
                var pass = reviewPasses.Length >= ReviewCalls && reviewPasses[ReviewCalls - 1];
                var score = pass ? 0.95 : 0.20;
                return Json(new { output = new[] { new { content = new[] {
                    new { type = "output_text", text = JsonSerializer.Serialize(
                        new { pass, score, issues = pass ? Array.Empty<string>() :
                            ["Car is not in a profile view"] }) } } } },
                    usage = new { input_tokens = 100, output_tokens = 30 } });
            }
            throw new InvalidOperationException("Unexpected API endpoint " + path);
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value),
                Encoding.UTF8, "application/json")
        };
    }

    private static string Manifest => JsonSerializer.Serialize(new
    {
        assets = new[] { new
        {
            path = "public/assets/car.png",
            prompt = "Realistic full car side profile on a transparent background for the racing game",
            criteria = new[] { "Car is fully visible", "Exact side profile" },
            size = "1024x1024", transparent = true
        }}
    });

    private static async Task<(AgentDbContext db, AgentTask task, string path)> Setup()
    {
        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        var db = new AgentDbContext(options);
        var task = new AgentTask { Id = Guid.NewGuid(), RepositoryUrl = "https://github.com/test/repo",
            Status = "running", EnableImageGeneration = true,
            ImageBudgetUsd = 0.35m, CreatePullRequest = true };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        var path = Path.Combine(Path.GetTempPath(), "image-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        await File.WriteAllTextAsync(Path.Combine(path, "developer-team.assets.json"), Manifest);
        return (db, task, path);
    }

    private static ImagePipeline Pipeline(FakeImageApi api) => new(
        new HttpClient(api),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["OpenAI:ApiKey"] = "test-key" }).Build());

    [Fact]
    public async Task RejectsBadDraftTwiceWithoutEverInvokingExpensiveFinal()
    {
        var (db, task, path) = await Setup();
        try
        {
            using (db)
            {
                var api = new FakeImageApi(false, false);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Pipeline(api).RunAsync(task, path, db, CancellationToken.None));
                Assert.Equal(2, api.DraftCalls);
                Assert.Equal(2, api.ReviewCalls);
                Assert.Equal(0, api.EditCalls);
                Assert.Equal("rejected", task.ImageStatus);
                Assert.Equal(0.054m, task.ImageReservedUsd);
                Assert.False(File.Exists(Path.Combine(path, "public/assets/car.png")));
                Assert.Single(db.GeneratedAssets);
            }
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task ApprovedDraftTriggersOneHighQualityReferenceEdit()
    {
        var (db, task, path) = await Setup();
        try
        {
            using (db)
            {
                var api = new FakeImageApi(false, true);
                await Pipeline(api).RunAsync(task, path, db, CancellationToken.None);
                Assert.Equal(2, api.DraftCalls);
                Assert.Equal(2, api.ReviewCalls);
                Assert.Equal(1, api.EditCalls);
                Assert.Equal("/v1/images/edits", api.Endpoints.Last());
                Assert.True(File.Exists(Path.Combine(path, "public/assets/car.png")));
                Assert.Equal("completed", task.ImageStatus);
                Assert.Equal(0.324m, task.ImageReservedUsd);
                Assert.Equal(1, db.GeneratedAssets.Single().FinalAttempts);
                Assert.Equal(123L * 2 + 456, db.GeneratedAssets.Single().ImageInputTokens);
            }
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task NoManifestMeansNoImagesAndNoApiCalls()
    {
        var (db, task, path) = await Setup();
        try
        {
            File.Delete(Path.Combine(path, "developer-team.assets.json"));
            using (db)
            {
                var api = new FakeImageApi();
                await Pipeline(api).RunAsync(task, path, db, CancellationToken.None);
                Assert.Empty(api.Endpoints);
                Assert.Equal("skipped", task.ImageStatus);
            }
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task LowBudgetStopsBeforeExpensiveEdit()
    {
        var (db, task, path) = await Setup();
        task.ImageBudgetUsd = 0.06m;
        try
        {
            using (db)
            {
                var api = new FakeImageApi(true);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Pipeline(api).RunAsync(task, path, db, CancellationToken.None));
                Assert.Equal(0, api.EditCalls);
                Assert.Equal(1, api.DraftCalls);
                Assert.Equal(1, api.ReviewCalls);
            }
        }
        finally { Directory.Delete(path, true); }
    }
    [Fact]
    public void TransparentImagesNeverUseOpaqueOnlyModel()
    {
        Assert.Equal("gpt-image-1.5", ImagePipeline.ModelForAsset(true,
            "gpt-image-2", "gpt-image-1.5"));
        Assert.Equal("gpt-image-2", ImagePipeline.ModelForAsset(false,
            "gpt-image-2", "gpt-image-1.5"));
    }

    [Fact]
    public async Task FailedTaskCanReuseStoredBriefWithoutNewDesignerCall()
    {
        var (db, task, path) = await Setup();
        try
        {
            File.Delete(Path.Combine(path, "developer-team.assets.json"));
            db.GeneratedAssets.Add(new GeneratedAsset
            {
                Id = Guid.NewGuid(), TaskId = task.Id, Status = "failed",
                Path = "public/assets/car.png",
                Prompt = "Realistic full car side profile on a transparent background for the racing game",
                CriteriaJson = "[\\"Car is fully visible\\",\\"Exact side profile\\"]",
                Size = "1024x1024", Transparent = true
            });
            await db.SaveChangesAsync();
            task.ImageReservedUsd = 0.015m; // previous failed request's reservation
            var api = new FakeImageApi(true);
            await Pipeline(api).RunAsync(task, path, db, CancellationToken.None, retry: true);
            Assert.Equal(1, api.DraftCalls);
            Assert.Equal(1, api.EditCalls);
            Assert.Equal("completed", task.ImageStatus);
            Assert.True(File.Exists(Path.Combine(path, "public/assets/car.png")));
            Assert.Equal(2, db.GeneratedAssets.Count());
            Assert.Equal(0.312m, task.ImageReservedUsd);
        }
        finally { db.Dispose(); Directory.Delete(path, true); }
    }

}
