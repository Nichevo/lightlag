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
near and far, to answer "is hiding worth its slowness?".

The games are computer against computer: the rival AI (M6) runs both sides, each with its own randomly
picked personality. The summary counts who won, how and on what day, and has a balance table by
personality. Each game's day-by-day numbers go to `stats\game-NNN-seed-S.csv` in the run folder.
Other match-ups for a batch run: `--pilot easy` (both Easy), `--pilot easy-v-normal` (an Easy player side
against a Normal rival), `--pilot test-v-ai` (the old random test pilot against the rival) and
`--pilot test` (the test pilot on both sides).

Races: `--player-race` and `--rival-race` take `human`, `hive`, `lattice` or `random` (each game rolls its own);
`--third-race` adds a second rival for a three-race game (`none` by default). A
batch run plays humans on both sides unless asked; the nightly run plays random races, so both races get
played every night, and its summary has a Races table (wins by race, mirror games left out).

To watch one game's AI decisions: `Lightlag.Headless ai-trace --seed S --days D --every N [--easy]` prints
each side's goal and each fleet's job every N days.

## Screen panel timing

`Lightlag.exe -- --panel-bench` (developer mode) plays 150 game days (`BENCH_DAYS` changes it), then opens
each view in turn (map with and without the side panel, a fleet selected, a planet selected, and the
Empire, Planets, Fleets, Research, Intel and Progress screens) and prints one `BENCH` line per view with the
HUD and process time per frame and the five slowest parts of the HUD refresh. Use it after changing a panel: a
panel that rebuilds its controls every frame shows up at once.

## Stats and the rival's thinking in reports

In a full game, an F8 report also holds `stats.csv` (each side at the end of each day: Industry, income,
planets, ships, power, research, ships built, lost and destroyed, battles, Industry earned and spent,
planets claimed and lost, shipyard armour) and `rival-mind.md` (the rival's personality, attack ratio,
guesses, fleet jobs and recent thoughts). In the game, F7 shows every fleet plus each rival fleet's job
on the map and a "rival mind" panel.

Target: under a minute per headless game, so a night covers hundreds of games.

## Choice log and the "what matters" table

Every full game keeps a choice log: for each side, every building laid down, research idea picked, ship
built (with its fittings), the rival's leanings for the game, zones drawn, freighters caught, and each
battle (odds going in, ships and losses on each side, the fleets' makeup and pull-back presets, and how it
ended). F8 reports hold it as `choices.csv`; batch and nightly runs write one per game next to the stats
CSV (`stats/game-NNN-seed-S-choices.csv`).

Computer sides roll leanings at the start of each game (missiles or railguns, a favoured research field,
more or fewer escorts and big ships, bold or careful odds, a pull-back preset) that drift a little each
day, and pick research from the options within a small band of the best (10 % in headless runs, 4 % in
normal games, none on Easy). All of it uses the game's seeded random numbers, so replays still match.

The batch summary gains a "Fights" section (battles, endings, crippled and lost ships, pull-backs,
warships at the end, raiding) and a "What matters" table: for each choice, how often it was picked, the
win rate with and without it, and battle win rate and ships lost per battle with and without it. Gaps of
15 points or more are flagged, choices nobody picked are listed as dead, and rows from fewer than 30 games
either way are marked thin. Personality and its research list go together, so read research rows next to
the personality rows.

## Balance audit: goods and heat

Every full game keeps a goods ledger per side (in the save): how much of each good was mined or made,
captured, salvaged or skimmed, and how much was used and by what (each kind of building, power plants,
ship fuel, tanker fuel, ships, refits, repairs, research), plus output lost because the stores were full.
The batch summary ends with two audit tables: goods (made, used, lost to full stores, left at the end, and
where it came from and went, per side per game) and heat (overheats per side per game, split into in
battle, with engines burning and running cold; and for each class how much of its heat and power budget
the fits in use take).

Fuel types are on by default; `--single-fuel` plays a batch with one fuel for every engine.

## The overnight "what matters" report

Before the detail, the summary opens with "What matters tonight", and the same report is written as a
page, `summary.html`, in the run folder. Targets take one hour of play as about one game year.

- Headline numbers against their targets: battles per game year (2 to 4), battles ending in a wipe (under a
  third), warships per side at mid-game (6 to 12), contacts that ended before a shot, shipyard days stalled
  on materials, and games won.
- How battles ended (wiped out, one side withdrew, both withdrew, drifted apart), the pull-back presets
  used and how Hold on went; each fight's formation (fights, wins, railgun volleys blocked by the fleet's
  own ships); and fleet size against results (1, 2, 3-5, 6+ ships: wins, losses, freighters caught).
- Warships per side over the game (average of both sides at each tenth; `warships` is also a column of the
  stats CSV).
- The what-matters table with kind, choice, games, win rate with and without, gap size and flags
  ("Choice that matters", "Thin: under 30 games", "Dead: never picked"), and suggested next tunes the
  runner writes from all of the above.

The balance table has one row per personality (Raider, Expander, Turtle, Trader, Hunter, Opportunist;
profiles in `ai.json`).
