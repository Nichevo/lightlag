using Lightlag.Sim.Core;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The Empire menu is gone: Needs you items count on the menu that now holds their fix.</summary>
public class MenuHomesTests
{
    private static NeedItem Item(AutoArea area, int? fleet = null, int? body = null) =>
        new() { Key = "test", Text = "test", Area = area, FleetId = fleet, BodyId = body };

    [Fact]
    public void Needs_you_items_count_on_the_menu_that_holds_their_fix()
    {
        var w = new World();
        Assert.Equal(NeedScreens.Shipyard, NeedScreens.For(w, Item(AutoArea.Shipyard)));
        Assert.Equal(NeedScreens.Ships, NeedScreens.For(w, Item(AutoArea.Designs)));
        Assert.Equal(NeedScreens.Fleets, NeedScreens.For(w, Item(AutoArea.Ground)));
        Assert.Equal(NeedScreens.Fleets, NeedScreens.For(w, Item(AutoArea.Boarding)));
        Assert.Equal(NeedScreens.Planets, NeedScreens.For(w, Item(AutoArea.Budget, body: 3)));
        Assert.Equal(NeedScreens.Budget, NeedScreens.For(w, Item(AutoArea.Budget)));
    }
}
