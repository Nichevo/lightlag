using Lightlag.Sim.Core;
using Xunit;

namespace Lightlag.Sim.Tests;

public class ShipArtTests
{
    private static string ArtFolder()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Lightlag.sln"))) root = Path.GetDirectoryName(root)!;
        return Path.Combine(root, "game", "Art", "Ships");
    }

    [Fact]
    public void Every_class_resolves_to_a_drawing_that_exists()
    {
        var folder = ArtFolder();
        foreach (var name in ShipArt.Drawings)
        {
            Assert.True(File.Exists(Path.Combine(folder, name + ".svg")), $"missing {name}.svg");
            Assert.True(File.Exists(Path.Combine(folder, name + "-cutaway.svg")), $"missing {name}-cutaway.svg");
        }
        foreach (var cls in GameData.ShipClasses.Values)
            Assert.Contains(ShipArt.For(cls), ShipArt.Drawings);
    }

    [Fact]
    public void Classes_without_their_own_drawing_borrow_the_nearest_hull()
    {
        Assert.True(ShipArt.HasOwn(GameData.ShipClass("gun-frigate")));
        Assert.Equal("frigate", ShipArt.For(GameData.ShipClass("gun-frigate")));
        var tender = GameData.ShipClass("supply-tender");
        Assert.False(ShipArt.HasOwn(tender));
        Assert.Equal("tanker", ShipArt.For(tender));
        Assert.Equal("cruiser", ShipArt.For(GameData.ShipClass("capital")));
        Assert.Equal("escort", ShipArt.For(GameData.ShipClass("picket")));
    }
}
