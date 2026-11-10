# Notes for working on Lightlag

- Privacy: never put personal information about the owner (name, email, location, hardware, local
  setup) into this repo: code, comments, docs, configs, test data, commit messages or PRs.
- The simulation (`src/Lightlag.Sim`) must never reference Godot. Godot code in `game/` only draws,
  reads input and issues commands.
- Determinism: all randomness from `SimRandom`; all world changes through `GameCommand`s; no
  wall-clock time in the simulation; trig and powers via `DetMath`, never `Math.Sin` etc.
  (they differ between Windows and Linux). `dotnet test` includes replay checks.
- Add a rule to `RuleBook.Default()` when a bug could have been caught by a state check.
- Log with an area and a short message; put details in the `data` object, not in the message.
- Scope is frozen to Plan 2, "Second playable" (milestones M8-M14 in the plan doc); new ideas go to
  the design doc's parking list with the milestone they belong to.
- Checks before pushing: `dotnet build Lightlag.sln`, `dotnet test Lightlag.sln`, and
  `dotnet build game/Lightlag.csproj`.
