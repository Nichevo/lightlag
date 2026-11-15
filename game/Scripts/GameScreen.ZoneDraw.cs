using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Drawing a zone on the map: click its centre, drag out its size (a ring shows it), let go to place it.
/// Right-click or Esc calls it off. Used for a new zone (from the fleet's zone menu) and to redraw one.
/// </summary>
public partial class GameScreen
{
    private sealed class ZoneDraw
    {
        public int? FleetId;
        public int? ZoneId;
        public ZoneKind Kind;
        public bool Pin;
        public Vec2? Center;
        public double RadiusMm;
    }

    private ZoneDraw? _zoneDraw;

    /// <summary>Default size of a zone placed with a click and no drag.</summary>
    private const double ClickZoneMm = 300;

    /// <summary>A zone is being drawn on the map.</summary>
    public bool DrawingZone => _zoneDraw != null;

    /// <summary>
    /// Start drawing a zone: a new one for the fleet (zoneId null), or a new place and size for zone zoneId.
    /// pin: the zone moves with the planet or moon nearest its centre.
    /// </summary>
    public void StartZoneDraw(int? fleetId, int? zoneId, ZoneKind kind, bool pin = false)
    {
        _zoneDraw = new ZoneDraw { FleetId = fleetId, ZoneId = zoneId, Kind = kind, Pin = pin };
        // The map must be in view to draw on it.
        ShowFleets = false;
        ShowPlanets = false;
        PlanetScreenId = null;
        ShowResearch = false;
        ShowShips = false;
        _map.QueueRedraw();
    }

    public void CancelZoneDraw()
    {
        _zoneDraw = null;
        _map.ZoneDraft = null;
        _map.QueueRedraw();
    }

    /// <summary>Map input while a zone is being drawn (true: used).</summary>
    private bool ZoneDrawInput(InputEvent e)
    {
        if (_zoneDraw is not ZoneDraw d) return false;
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
                CancelZoneDraw();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } down:
                d.Center = _camera.ToWorld(down.Position);
                d.RadiusMm = 0;
                UpdateZoneDraft(down.Position);
                return true;
            case InputEventMouseMotion motion:
                UpdateZoneDraft(motion.Position);
                return d.Center != null;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } up when d.Center is Vec2 c:
                var r = Vec2.Distance(c, _camera.ToWorld(up.Position));
                // A plain click (under 8 pixels of drag) places a zone of the usual size.
                if (r * _camera.Scale < 8) r = d.ZoneId is int zid && Sim.World.Zones.Find(z => z.Id == zid) is Zone old ? old.RadiusMm : ClickZoneMm;
                FinishZoneDraw(d, c, r);
                return true;
        }
        return false;
    }

    private void UpdateZoneDraft(Vector2 mouse)
    {
        if (_zoneDraw is not ZoneDraw d) return;
        var color = ZonePanel.KindColor(d.Kind);
        var verb = d.ZoneId != null ? "Redraw the zone" : $"Draw a {Lanes.KindWord(d.Kind).ToLowerInvariant()} zone";
        if (d.Center is Vec2 c)
        {
            var r = Vec2.Distance(c, _camera.ToWorld(mouse));
            d.RadiusMm = Math.Clamp(r, 20, 2000);
            _map.ZoneDraft = (_camera.ToScreen(c), (float)(d.RadiusMm * _camera.Scale), color,
                $"{verb}: radius {Format.Distance(d.RadiusMm)} · let go to place it · right-click or Esc to stop");
        }
        else
        {
            var pinText = d.Pin && Lanes.AnchorFor(Sim.World, _camera.ToWorld(mouse)) is Body b ? $" (moves with {b.Name})" : "";
            _map.ZoneDraft = (mouse, 0, color, $"{verb}{pinText}: click its centre and drag out its size · a plain click makes one {Format.Distance(ClickZoneMm)} across · right-click or Esc to stop");
        }
        _map.QueueRedraw();
    }

    private void FinishZoneDraw(ZoneDraw d, Vec2 center, double radius)
    {
        var w = Sim.World;
        _zoneDraw = null;
        _map.ZoneDraft = null;
        if (d.ZoneId is int id && w.Zones.Find(z => z.Id == id) is Zone z)
            IssueNow(new EditZoneCommand(Side.Player, z.Id, center, radius, null, null, z.T, z.Hold, z.Prey), "redraw zone");
        else
        {
            var pin = d.Pin ? Lanes.AnchorFor(w, center)?.Id : null;
            IssueNow(new CreateZoneCommand(Side.Player, d.Kind, center, radius, FleetId: d.FleetId, AnchorBodyId: pin), "draw zone");
        }
        _map.QueueRedraw();
    }
}
