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
}
