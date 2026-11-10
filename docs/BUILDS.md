# Builds, updates and overnight runs

## Automatic builds

`.github/workflows/build.yml` runs on every push and pull request:

1. Simulation tests.
2. A 20-game headless batch with strict rule checks, and a one-pass battle sweep.
3. Installs Godot 4.7.2 (.NET) and builds the game.
4. Smoke test: runs the real game headless with `--smoke-test` (plays 30 days, writes a report, quits).
5. Exports the Windows build and bundles the headless runner and the launcher scripts into a zip.
6. On `main` only: publishes it as a GitHub release `v0.0.<run number>`, marked latest.

If a step fails, the logs and reports it produced are attached to the run as `failure-reports`.

## Update and Play

`tools/UpdateAndPlay.ps1` (the "Lightlag" desktop shortcut) asks GitHub for the latest release. If it
is newer than the installed one it downloads it into `builds\current` and moves the old one to
`builds\previous`. Then it starts the game with `--data-dir` pointing at `play\data`.

- Offline: it just plays the installed build.
- `-Rollback` (the "previous build" shortcut): swaps current and previous.
- `-NoUpdate`, `-NoLaunch`, `-Force`: as named.
- Each update also refreshes the launcher scripts in the play folder from the new build.

## Overnight runs

`Register-Nightly.ps1` adds a Windows scheduled task ("Lightlag nightly run", default 01:30) that
runs `Run-Nightly.ps1`: update to the newest build, then

```
builds\current\headless\Lightlag.Headless.exe nightly --data-dir data
```

which plays up to 200 games of 365 days each (stopping after 6 hours) with rule checks every tick.
Results go to `data\nightly\<time>_nightly\` (summary, log, a crash report per failed game), and the
summary is copied to `data\nightly\latest-summary.md`. After that a battle sweep fights every fleet mix
with every approach and missile brain in the sandbox and writes `data\nightly\latest-battles.md` (win rates)
and a `battles.csv`. Its last section is the ambush sweep: drifting in cold against burning in, from
near and far, to answer "is hiding worth its slowness?". Until the rival AI exists, a "test pilot"
plays both sides at random (flights, claims, attacks on shipyards, building and research) so there is
something to simulate; the summary counts which side won and on what day.

Target: under a minute per headless game, so a night covers hundreds of games.
