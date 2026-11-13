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
        foreach (var name in ShipArt.Sails) Assert.True(File.Exists(Path.Combine(folder, name + ".svg")), $"missing {name}.svg");
        foreach (var cls in GameData.ShipClasses.Values)
            Assert.Contains(ShipArt.For(cls), ShipArt.Drawings.Concat(ShipArt.Sails));
    }

    [Fact]
    public void Each_race_skin_has_its_drawings_and_falls_back_to_human()
    {
        var folder = ArtFolder();
        foreach (var race in ShipArt.SkinnedRaces)
            foreach (var name in ShipArt.RaceDrawings)
                Assert.True(File.Exists(Path.Combine(folder, race, name + ".svg")), $"missing {race}/{name}.svg");
        foreach (var cls in GameData.ShipClasses.Values)
            foreach (var race in ShipArt.SkinnedRaces)
            {
                var art = ShipArt.For(cls, race);
                var path = art.Contains('.') ? Path.Combine(folder, art.Replace('.', Path.DirectorySeparatorChar) + ".svg") : Path.Combine(folder, art + ".svg");
                Assert.True(File.Exists(path), $"{cls.Id} as {race}: {art}");
                Assert.InRange(ShipArt.HullShare(art), 0.5f, 1f);
            }
        Assert.Equal("hive.escort", ShipArt.For(GameData.ShipClass("gun-frigate"), Races.Hive));
        Assert.Equal("lattice.cruiser", ShipArt.For(GameData.ShipClass("cruiser"), Races.Lattice));
        Assert.Equal("cruiser", ShipArt.For(GameData.ShipClass("cruiser"), Races.Human));
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
