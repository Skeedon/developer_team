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

}
