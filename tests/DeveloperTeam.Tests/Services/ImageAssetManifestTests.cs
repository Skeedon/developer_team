using DeveloperTeam.Services;
using Xunit;

namespace DeveloperTeam.Tests.Services;

public class ImageAssetManifestTests
{
    private static string Payload(string path, string size = "1024x1024") =>
        System.Text.Json.JsonSerializer.Serialize(new { assets = new[] { new {
            path, prompt = "Side profile of a detailed pixel-art car for the garage scene",
            size, transparent = true, criteria = new[] { "Full car visible", "Profile view" } } } });
    [Fact]
    public void AcceptsSafeSingleAsset()
    {
        var assets = ImageAssetManifest.Parse(Payload("public/assets/car.png"));
        Assert.Single(assets);
        Assert.True(assets[0].Transparent);
    }
    [Theory]
    [InlineData("../car.png")]
    [InlineData("public/assets/../../config/secret.png")]
    [InlineData("/tmp/evil.png")]
    [InlineData("public/assets/car.svg")]
    [InlineData("public/assets//car.png")]
    [InlineData("public/assets/car.png", "512x512")]
    public void RejectsUnsafeManifest(string path, string size = "1024x1024") =>
        Assert.Throws<InvalidOperationException>(() => ImageAssetManifest.Parse(Payload(path,size)));
    [Fact]
    public void RejectsOverBudgetAssetCount()
    {
        var json = "{\"assets\":[" + Payload("assets/a.png").Replace("{\"assets\":[","").TrimEnd('}',']') + "," +
            Payload("assets/b.png").Replace("{\"assets\":[","").TrimEnd('}',']') + "]}";
        Assert.ThrowsAny<Exception>(() => ImageAssetManifest.Parse(json,1));
    }
    [Fact]
    public void ResolvesOnlyInsideWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.StartsWith(root, ImageAssetManifest.ResolveSafeOutput(root, "public/assets/car.png"));
        Assert.Throws<InvalidOperationException>(() => ImageAssetManifest.ResolveSafeOutput(root, "../escape.png"));
    }
    [Fact]
    public void AllowsSeveralDistinctCarImagesWhenEachIsOneCar()
    {
        var asset1 = new {
            path = "public/assets/car-bmw.png",
            prompt = "One BMW E36 side profile facing left, fully visible on a transparent background",
            size = "1024x1024", transparent = true,
            criteria = new[] { "Single car only", "Exact side view" }
        };
        var asset2 = new {
            path = "public/assets/car-skyline.png",
            prompt = "One Nissan Skyline side profile facing left, fully visible on a transparent background",
            size = "1024x1024", transparent = true,
            criteria = new[] { "Single car only", "Exact side view" }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(new { assets = new[] { asset1, asset2 } });
        Assert.Equal(2, ImageAssetManifest.Parse(json).Count);
    }

    [Theory]
    [InlineData("Cars drawn as one sprite sheet in a grid with each car in a cell")]
    [InlineData("All cars must be in a single atlas; wheel baseline y=390")]
    [InlineData("Draw multiple cars as a 3x2 grid")]
    [InlineData("Автомобили в атласе, все машины в отдельных ячейках")]
    public void RejectsImpossibleCarSheetBeforePaidCalls(string description)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { assets = new[] { new {
            path = "public/assets/cars.png",
            prompt = "Several cars for the game interface. " + description,
            criteria = new[] { "Each car has correct placement" },
            size = "1024x1024", transparent = true
        } } });
        var error = Assert.Throws<InvalidOperationException>(() => ImageAssetManifest.Parse(json));
        Assert.Contains("ONE PNG asset per car", error.Message);
    }


    [Fact]
    public void AcceptsMoreThanSixDetailedCriteriaWithoutLosingRequirements()
    {
        var requirements = Enumerable.Range(1, 10)
            .Select(n => "Requirement " + n + ": " + new string('x', 270)).ToArray();
        var json = System.Text.Json.JsonSerializer.Serialize(new { assets = new[] { new {
            path = "public/assets/car.png",
            prompt = "A single car strictly from the left in semi-realistic game style",
            size = "1024x1024", transparent = true, criteria = requirements
        }}});
        var asset = Assert.Single(ImageAssetManifest.Parse(json));
        Assert.Equal(10, asset.Criteria.Length);
        Assert.Equal(requirements[^1], asset.Criteria[^1]);
        Assert.True(asset.Criteria[0].Length > 250);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("blank")]
    public void FallsBackToPromptBasedChecksWhenCriteriaMissingOrEmpty(string mode)
    {
        object asset = mode == "absent"
            ? new { path = "assets/car.png", prompt = "One realistic passenger car in complete side profile" }
            : new { path = "assets/car.png", prompt = "One realistic passenger car in complete side profile",
                criteria = mode == "empty" ? Array.Empty<string>() : ["", "  ", " "] };
        var json = System.Text.Json.JsonSerializer.Serialize(new { assets = new[] { asset } });
        var parsed = Assert.Single(ImageAssetManifest.Parse(json));
        Assert.Equal(2, parsed.Criteria.Length);
        Assert.All(parsed.Criteria, x => Assert.Contains("prompt", x));
    }

    [Fact]
    public void TrimsAndDeduplicatesDetailedCriteria()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { assets = new[] { new {
            path = "assets/car.png", prompt = "Single detailed coupe in pure side profile",
            criteria = new[] { " Strict side view ", "strict side view", "", " wheels fully visible " }
        }}});
        var asset = Assert.Single(ImageAssetManifest.Parse(json));
        Assert.Equal(new[] { "Strict side view", "wheels fully visible" }, asset.Criteria);
    }

    [Fact]
    public void AcceptsSingleStringAndStructuredCriterion()
    {
        const string json = """
        {"assets":[{"path":"assets/car.png",
         "prompt":"Single detailed passenger coupe in a strict side profile",
         "criteria":[{"description":"Correct side profile"},"No background"]}]}
        """;
        var asset = Assert.Single(ImageAssetManifest.Parse(json));
        Assert.Equal(new[] { "Correct side profile", "No background" }, asset.Criteria);
    }

}
