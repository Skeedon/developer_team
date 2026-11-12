using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DeveloperTeam.Data;
using DeveloperTeam.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeveloperTeam.Services;

/// <summary>
/// Explicitly opted-in, bounded image production:
/// low-quality draft -> independent vision evaluation -> high-quality edit of approved draft.
/// No high-quality request is made before the draft passes evaluation.
/// Budget values are conservative estimates/reservations, not provider billing statements.
/// </summary>
public sealed class ImagePipeline(HttpClient http, IConfiguration config)
{
    // Reservations include overhead for text input and reference-image processing.
    public const decimal DraftReserveUsd = 0.015m;
    public const decimal EvaluatorReserveUsd = 0.012m;
    public const decimal FinalSquareReserveUsd = 0.270m;
    public const decimal FinalLandscapeReserveUsd = 0.230m;
    public const int MaxDrafts = 2;
    public const double AcceptanceThreshold = 0.80;

    public static decimal FinalReserve(string size) => size == "1024x1024"
        ? FinalSquareReserveUsd : FinalLandscapeReserveUsd;

    public static bool CanReserve(decimal already, decimal amount, decimal budget) =>
        amount > 0 && budget > 0 && already + amount <= budget;

    public async Task RunAsync(AgentTask task, string workspace, AgentDbContext db,
        CancellationToken ct, bool retry = false)
    {
        if (!task.EnableImageGeneration) return;
        var manifestPath = Path.Combine(workspace, "developer-team.assets.json");
        string manifest;
        if (File.Exists(manifestPath))
        {
            manifest = await File.ReadAllTextAsync(manifestPath, ct);
        }
        else if (retry)
        {
            // Older failed tasks had their manifest deleted before making the API call.
            // Rebuild only from the validated brief already stored in PostgreSQL.
            var previous = await db.Set<GeneratedAsset>().AsNoTracking()
                .Where(a => a.TaskId == task.Id)
                .OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct);
            if (previous is null)
                throw new InvalidOperationException("No saved image brief is available for retry.");
            manifest = JsonSerializer.Serialize(new { assets = new[] {
                new { path = previous.Path, prompt = previous.Prompt,
                    criteria = JsonSerializer.Deserialize<string[]>(previous.CriteriaJson) ?? [],
                    size = previous.Size, transparent = previous.Transparent }
            } });
        }
        else
        {
            // Designer chose CSS/SVG only: absolutely no image calls.
            task.ImageStatus = "skipped";
            await db.SaveChangesAsync(ct);
            return;
        }
        var assets = ImageAssetManifest.Parse(manifest);
        if (assets.Count == 0)
        {
            task.ImageStatus = "skipped";
            await db.SaveChangesAsync(ct);
            return;
        }
        if (!task.CreatePullRequest)
            throw new InvalidOperationException("Generated image assets require a PR task for review.");

        var key = config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenAI API key missing for image generation.");
        var draftModel = config["Images:DraftModel"] ?? "gpt-image-2";
        var finalModel = config["Images:FinalModel"] ?? "gpt-image-2";
        var transparentModel = config["Images:TransparentModel"] ?? "gpt-image-1.5";
        var evaluatorModel = config["Images:EvaluatorModel"] ?? "gpt-4.1-mini";
        // GPT Image 2 transparency is not available to every account; the API can
        // return 400 invalid_value for background=transparent. Use a model with
        // established transparency support instead of quietly making opaque sprites.
        // Fixed, priced models: do not allow arbitrary overrides to bypass budgeting.
        if (draftModel != "gpt-image-2" || finalModel != "gpt-image-2" ||
            transparentModel != "gpt-image-1.5" || evaluatorModel != "gpt-4.1-mini")
            throw new InvalidOperationException("Unsupported image model configuration for the current budget estimator.");

        foreach (var brief in assets)
        {
            // Select a supported model BEFORE reserving funds or making a request.
            // Both draft and final use the same transparency-capable image family.
            var selectedDraftModel = ModelForAsset(brief.Transparent, draftModel, transparentModel);
            var selectedFinalModel = ModelForAsset(brief.Transparent, finalModel, transparentModel);
            // Fail before incurring costs if the output file is unsafe or already exists.
            var output = ImageAssetManifest.ResolveSafeOutput(workspace, brief.Path);
            if (File.Exists(output))
                throw new InvalidOperationException("Image output already exists: " + brief.Path);

            var asset = new GeneratedAsset
            {
                Id = Guid.NewGuid(), TaskId = task.Id, Path = brief.Path, Prompt = brief.Prompt,
                CriteriaJson = JsonSerializer.Serialize(brief.Criteria), Size = brief.Size,
                Transparent = brief.Transparent, Status = "draft"
            };
            db.Set<GeneratedAsset>().Add(asset);
            task.ImageStatus = "draft";
            await db.SaveChangesAsync(ct);
            var draftsDir = Path.Combine(workspace, ".git", "developer-team-image-drafts");
            Directory.CreateDirectory(draftsDir);
            var draftFile = Path.Combine(draftsDir, asset.Id.ToString("N") + ".png");
            try
            {
                string feedback = "";
                byte[]? accepted = null;
                for (var attempt = 1; attempt <= MaxDrafts; attempt++)
                {
                    Reserve(task, asset, DraftReserveUsd);
                    asset.DraftAttempts = attempt;
                    asset.Status = task.ImageStatus = "draft";
                    await db.SaveChangesAsync(ct);
                    var prompt = brief.Prompt +
                        "\nThis is a low quality composition draft. Composition and must-have elements matter most." +
                        (feedback.Length > 0 ? "\nCorrect these visual issues: " + feedback : "");
                    var draft = await GenerateAsync(key, selectedDraftModel, brief, prompt, ct);
                    AddTokens(asset, draft);
                    await File.WriteAllBytesAsync(draftFile, draft.Bytes, ct);

                    Reserve(task, asset, EvaluatorReserveUsd);
                    asset.Status = task.ImageStatus = "reviewing";
                    await db.SaveChangesAsync(ct);
                    var verdict = await EvaluateAsync(key, evaluatorModel, brief, draft.Bytes, ct);
                    asset.EvaluatorInputTokens += verdict.InputTokens;
                    asset.EvaluatorOutputTokens += verdict.OutputTokens;
                    asset.Score = verdict.Score;
                    asset.Review = verdict.Details;
                    await db.SaveChangesAsync(ct);
                    if (verdict.Pass && verdict.Score >= AcceptanceThreshold)
                    {
                        accepted = draft.Bytes;
                        break;
                    }
                    feedback = verdict.Feedback;
                }
                if (accepted is null)
                {
                    asset.Status = task.ImageStatus = "rejected";
                    asset.Error = "Draft did not meet the requirements. No high-quality generation was charged.";
                    await db.SaveChangesAsync(ct);
                    throw new InvalidOperationException(asset.Error);
                }

                Reserve(task, asset, FinalReserve(brief.Size));
                asset.Status = task.ImageStatus = "finalizing";
                asset.FinalAttempts = 1;
                await db.SaveChangesAsync(ct);
                var final = await EditAsync(key, selectedFinalModel, brief, accepted, ct);
                AddTokens(asset, final);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllBytesAsync(output, final.Bytes, ct);
                asset.Status = task.ImageStatus = "completed";
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (asset.Status != "rejected")
                    asset.Status = task.ImageStatus = "failed";
                asset.Error ??= ex.Message;
                await db.SaveChangesAsync(ct);
                throw;
            }
        }
        // Remove only after successful generation, never before a failed paid request.
        if (File.Exists(manifestPath)) File.Delete(manifestPath);
    }

    public static string ModelForAsset(bool transparent, string opaqueModel, string transparentModel) =>
        transparent ? transparentModel : opaqueModel;

    private static void Reserve(AgentTask task, GeneratedAsset asset, decimal amount)
    {
        if (!CanReserve(task.ImageReservedUsd, amount, task.ImageBudgetUsd))
            throw new InvalidOperationException(
                "Image budget exceeded before next paid API call. Increase the task image budget explicitly.");
        task.ImageReservedUsd += amount;
        asset.ReservedUsd += amount;
    }

    private static void AddTokens(GeneratedAsset asset, ImageResult response)
    {
        asset.ImageInputTokens += response.InputTokens;
        asset.ImageOutputTokens += response.OutputTokens;
    }

    private sealed record ImageResult(byte[] Bytes, long InputTokens, long OutputTokens);
    private sealed record Verdict(bool Pass, double Score, string Details, string Feedback, long InputTokens, long OutputTokens);

    private async Task<ImageResult> GenerateAsync(string key, string model,
        ImageAssetManifest.Asset brief, string prompt, CancellationToken ct)
    {
        return await ImageRequestAsync(key, "images/generations", new
        {
            model, prompt, n = 1, size = brief.Size, quality = "low",
            background = brief.Transparent ? "transparent" : "opaque", output_format = "png"
        }, ct);
    }

    private async Task<ImageResult> EditAsync(string key, string model,
        ImageAssetManifest.Asset brief, byte[] reference, CancellationToken ct)
    {
        var prompt = brief.Prompt +
            "\nProduce a polished high-quality final asset using the provided approved draft as the composition reference. " +
            "Preserve the subject, camera angle, proportions and layout. Meet these checks: " +
            string.Join("; ", brief.Criteria) + ". Do not add text unless explicitly required.";
        return await ImageRequestAsync(key, "images/edits", new
        {
            model, prompt, size = brief.Size, quality = "high",
            background = brief.Transparent ? "transparent" : "opaque",
            output_format = "png",
            images = new[] { new { image_url = "data:image/png;base64," + Convert.ToBase64String(reference) } }
        }, ct);
    }

    private async Task<ImageResult> ImageRequestAsync(string key, string endpoint, object payload, CancellationToken ct)
    {
        using var response = await SendAsync(key, endpoint, payload, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = document.RootElement;
        var encoded = root.GetProperty("data")[0].GetProperty("b64_json").GetString()
            ?? throw new InvalidOperationException("Image API returned empty image.");
        var image = Convert.FromBase64String(encoded);
        if (image.Length is < 100 or > 12_000_000 ||
            !image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidOperationException("Image API returned an invalid or oversized PNG.");
        var input = 0L; var output = 0L;
        if (root.TryGetProperty("usage", out var usage))
        {
            input = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : 0;
            output = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : 0;
        }
        return new ImageResult(image, input, output);
    }

    private async Task<Verdict> EvaluateAsync(string key, string model,
        ImageAssetManifest.Asset brief, byte[] image, CancellationToken ct)
    {
        var checks = string.Join("\n- ", brief.Criteria);
        var text = "You are an independent visual QA reviewer. Inspect the supplied draft image, not the prompt alone. " +
            "Decide whether it matches the required composition, style, and all listed criteria. " +
            "Be strict about missing subjects, incorrect perspective, important text, and transparency. " +
            "Return ONLY a JSON object with keys pass (boolean), score (number 0..1), " +
            "issues (array of short strings). No markdown. If uncertain, set pass=false.\n" +
            "Image brief: " + brief.Prompt + "\nRequirements:\n- " + checks;
        using var response = await SendAsync(key, "responses", new
        {
            model, max_output_tokens = 250,
            input = new[] { new { role = "user", content = new object[]
            {
                new { type = "input_text", text },
                new { type = "input_image", image_url = "data:image/png;base64," +
                    Convert.ToBase64String(image), detail = "low" }
            } } }
        }, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        var body = "";
        if (root.TryGetProperty("output", out var output))
            foreach (var message in output.EnumerateArray())
                if (message.TryGetProperty("content", out var content))
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("type", out var type) &&
                            type.GetString() == "output_text" &&
                            part.TryGetProperty("text", out var msg))
                            body += msg.GetString();
        var pass = false; var score = 0d;
        string[] issues = ["Vision evaluator did not return valid JSON."];
        try
        {
            using var result = JsonDocument.Parse(body);
            var obj = result.RootElement;
            pass = obj.GetProperty("pass").GetBoolean();
            score = obj.GetProperty("score").GetDouble();
            if (!double.IsFinite(score) || score is < 0 or > 1) { pass = false; score = 0; }
            issues = obj.GetProperty("issues").EnumerateArray()
                .Take(6).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            pass = false; score = 0;
        }
        var usage = root.TryGetProperty("usage", out var u) ? u : default;
        var inputTokens = usage.ValueKind == JsonValueKind.Object &&
            usage.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : 0;
        var outputTokens = usage.ValueKind == JsonValueKind.Object &&
            usage.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : 0;
        var detail = JsonSerializer.Serialize(new { pass, score, issues });
        return new Verdict(pass, score, detail, string.Join("; ", issues)[..Math.Min(1500,
            string.Join("; ", issues).Length)], inputTokens, outputTokens);
    }

    private async Task<HttpResponseMessage> SendAsync(string key, string endpoint, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/" + endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return response;
        var detail = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new InvalidOperationException("Image or vision API HTTP " + status + ": " +
            detail[..Math.Min(detail.Length, 300)]);
    }
}
