# Adding to the battle view

The battle view (U) has a row of cards along the bottom: Move, Railguns, Missiles, Pulse lances, Defence, Heat,
Formation and Orders. Each card is one small class. A new weapon or mechanic gets a new card (or a new row on an
existing card), and the screen handles the rest:

- placing the cards left to right, and folding them to one or two lines when they don't fit (rightmost first);
- pins: a click on a card's title keeps it open or folded, remembered in ui.cfg; right-click lets it fold by itself again;
- the fleet tabs: on the All fleets tab an order goes to every fleet, and a setting that differs between them reads "mixed";
- orders given while paused or still on their way under light lag show as chosen straight away;
- hovering a card lights its reach ring on the map.

The code lives in `game/Scripts/Battle/`:

| File | What it holds |
| --- | --- |
| `BattleCards.cs` | `BattleCard` (the base class), `BattleCards.Makers` (the list of cards), `CardContext`, `CardBuilder` |
| `BuiltInCards.cs` | The eight cards above; copy one to start a new card |
| `BattleScreen.cs` | The screen: header, tabs, card row and folding, layout |
| `BattleShips.cs` | Your ships (left) and the enemy by what you know (right), the odds bar |
| `BattleTimeline.cs` | When things arrive: a row per fleet, click a mark to pause there |
| `BattleShared.cs` | The reach rings (`EngagementRings`) and the labelled `StatBar` |

Map paths for the view are in `game/Scripts/MapView.Battle.cs`.

## A new card in four steps

1. **The sim first.** The setting is a field on `Fleet` and a `GameCommand` that changes it (see `SetDodgeCommand`).
   The battle view never changes the world itself.
2. **Write the card** in `BuiltInCards.cs` (or a file of its own in `game/Scripts/Battle/`):

   ```csharp
   public sealed class DecoyCard : BattleCard
   {
       public override string Id => "decoys";          // kept in ui.cfg for the pin: don't rename it later
       public override string Title => "Decoys";
       public override int Order => 45;                 // left to right: Move 10, Railguns 20, Missiles 30, Lances 35, Defence 40, Heat 50, Formation 60, Orders 90
       public override Color? Reach => null;            // a ring colour from EngagementRings to light on hover

       // Show the card only when a ship carries the system.
       public override bool Shows(CardContext c) => c.Ships.Any(s => Fitting.Spec(s).Decoys > 0);

       public override void Build(CardBuilder b)
       {
           b.Choice("Release", new (string, string, DecoyMode)[]
               {
                   ("Hold", "Keep them aboard", DecoyMode.Hold),
                   ("On missiles", "Release when missiles close in", DecoyMode.OnMissiles),
               },
               (c, f) => c.Pending<SetDecoysCommand>(f)?.Mode ?? f.Decoys,   // the value now, counting orders not yet applied
               (f, mode) => new SetDecoysCommand(f.Id, mode), "decoys");
           b.Line(c => $"Left {c.Ships.Sum(s => s.Decoys)}", Ui.Dim);
       }

       // One or two short lines for when the card is folded.
       public override string Folded(CardContext c) => $"{c.Ships.Sum(s => s.Decoys)} left";
   }
   ```

3. **Register it**: add `() => new DecoyCard(),` to `BattleCards.Makers` in `BattleCards.cs`.
4. **Check it**: `dotnet build game/Lightlag.csproj`, then open the battle view. The screenshot run
   (`--fix-shots=DIR` with `FIX_SAVE`, `FIX_FLEET` and `FIX_BATTLE`) saves `12-battle-view.png` and
   `12b-battle-view-all-fleets.png`.

## The rows a card can have (`CardBuilder`)

| Row | Use it for |
| --- | --- |
| `Choice(label, options, current, command, what)` | A setting with a few values, one button each (the one in force is lit) |
| `Dropdown(label, options, current, command, what)` | The same with too many values for buttons (formation shapes) |
| `Toggle(text, tip, current, command, what, visible)` | An on/off switch |
| `Actions((text, tip, command, danger), ...)` | One-off orders (eject sinks, pull back now); danger ones are red |
| `Line(text, color, size, colorNow)` | A line of text worked out every frame; an empty string hides it |
| `Custom(control, refresh)` | Anything else (the Move card's approach grid is one) |

`current` reads a fleet's value: use `c.Pending<TheCommand>(f)?.Value ?? f.Value` so a choice shows at once while
paused. `command` makes the order for one fleet (return null to skip that fleet). On the All fleets tab both run once
per fleet.

## Other things that plug in

- **A reach ring** for a new weapon: add it to `EngagementRings.For` in `BattleShared.cs` and return its colour from the
  card's `Reach`.
- **A new lane on the timeline**: add a time window to `EngagementInfo` in the sim (`Engagement.cs`) and a `Lane(...)` line
  in `BattleTimeline._Draw`.
- **A new approach choice**: add it in `Engagement.Options` in the sim; the Move card lists whatever the sim offers.
- **A new path on the map**: draw it in `MapView.Battle.cs`, in the same style as the others (see the note at the top of
  that file), and add a row to the legend in `BattleScreen.RefreshLegend`.
