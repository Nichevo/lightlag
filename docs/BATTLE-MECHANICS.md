# Adding a battle mechanic or a weapon system

A battle mechanic is a new order or rule in a fight (hold at rail range, point defence for the fleet or itself). A
weapon system is a new thing ships carry and fire (railguns, missiles, pulse lances). Both go in the same five places,
in this order. `docs/BATTLE-CARDS.md` covers the screen part in detail.

## A new mechanic

1. **Sim state and command.** A field on `Fleet` (or `Ship`) with a default that keeps old saves working, and one
   `GameCommand` that sets it, registered with `[JsonDerivedType]` at the top of `Commands.cs` (a test fails if you
   forget). Put new commands for fights in `Core/BattleMoves.cs`, beside the others.
2. **The rule.** Where it acts in a fight:
   - movement and range keeping: `Combat.Steer` (see `RailHoldTooClose`), or a `ChaseMode` with its distance in
     `FlightPlanner.ChaseDistance` and its name in `ChaseName`;
   - point defence and missiles: `Combat.ResolveMissile` (see `Formations.PdShare`);
   - slots, cover and screening: `Formations.Layout`, `Flagships`;
   - things that happen once an hour (rejoining, pulling back): `BattleMoves.Hourly`, `BattleRules.Hourly`.
   Numbers go in `Data/ship-classes.json` (and the record that reads them), with a line in the matching `_comment`.
3. **The rival.** The rival gives the same command; it never changes the world itself. Its battle choices are in
   `Mind.cs`: `BattleMovesFor` (run for each of its fleets) and `Approach` (how a hunt goes in). Base its choice only on
   what it knows (`RivalTech.Guess` for enemy reach, sightings), never on the player's real state.
4. **The screen.** A row on an existing card or a new card (`game/Scripts/Battle/BuiltInCards.cs`), or an approach choice
   in `Engagement.Options` (the Move card lists whatever the sim offers). Read the value with `c.Pending<TheCommand>(f)`
   so the choice shows at once while paused. Write the tooltip as the trade-off: what it gains and what it costs.
5. **Tests and words.** A test in `tests/Lightlag.Sim.Tests/BattleMovesTests.cs` (the rule acts, and the command sets
   it); a line in the test list for the playtest; a rule in `RuleBook.Default()` if a broken state could be checked.
   `dotnet test` runs the replay checks, which catch randomness that does not come from `SimRandom`.

## A new weapon system

1. **Data.** The module in `Data/ship-classes.json` (`modules`, with its slot, power and heat), its numbers in a block
   of their own (like `"weapons"` for pulse lances), and its research in `economy.json`. If it can be knocked out, add it
   to `Module` in `World.cs`.
2. **Firing and stopping it.** One file of its own in `Core/` (pulse lances are `Beams` in `Weapons.cs`): when it fires,
   what it hits, what stops it (dodging, point defence, shields), the heat it makes. Call it from `Combat.Fire`, as `Beams.Fire` is.
3. **Reach and intel.** Record how far it was seen to fire on the battle side (`BattleSide.RailReach` and friends) and
   feed `RivalTech.NoteReach`, so the other side learns its reach by fighting it; add it to `ReachGuess`.
4. **The screen.** A card that `Shows` only when a ship carries it, a reach ring (`EngagementRings.For`), and a timeline
   lane if it lands later than it fires (`EngagementInfo` plus `BattleTimeline`).
5. **The rival.** When it builds and fits it (`Mind` and the design picker) and when it uses it (`BattleMovesFor`).

## The mechanics so far

| Mechanic | Sim | Rival | Screen |
| --- | --- | --- | --- |
| Hold at rail range | `ChaseMode.RailRange`, `Combat.RailHold`, `Combat.RailHoldTooClose` | `Mind.Approach`: when its rails reach at least as far as it thinks theirs do and they carry lances | Move card (an approach choice) |
| Point defence for fleet or self | `Fleet.PdGuard`, `Formations.PdShare`, heat in `Combat.ResolveMissile` | `Mind.BattleMovesFor`: Self for a lone ship | Defence card, "PD for" |
| Protect a ship (a picked or the most damaged ship takes the safest slot; the flagship alone keeps its place) | `Fleet.ProtectWorst`, `Ship.Protect`, `Flagships.Worst`, `Formations.Guarded` | Protects its most damaged ship | Formation card, "Protect" and "Cover" |
| Split and run, split and hold back | `SplitOffCommand`, `BattleMoves.AfterSplit` | Its pull-back rules split off hurt ships the same way (`BattleRules.ShipRule`) | Your ships column, under the ticks |
| When done, rejoin | `Fleet.RejoinWhen`, `BattleMoves.Done`, `BattleMoves.Hourly` | Its split-off ships always rejoin | Fleet line in your ships column |
