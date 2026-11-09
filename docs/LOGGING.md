# Logging, rule checks, crash reports and replays

The aim: when anything goes wrong, there is enough on disk to see what happened and to make it
happen again, without having to describe it from memory.

## The log

`data/logs/lightlag_<time>.jsonl`, one JSON object per line. The last 30 session logs are kept.

```json
{"time":"2026-11-09T08:46:54.120Z","lvl":"warn","build":"0.0.12","seed":4711,"day":37,"tick":893,"area":"sim","msg":"Move order for a ship that does not exist","data":{"shipId":99}}
```

| Field | Meaning |
|---|---|
| `lvl` | `debug`, `info`, `warn`, `error`, `fatal` |
| `build` | The build that wrote it (matches the title screen) |
| `seed`, `day`, `tick` | Which game and when in it (1 tick = 1 game hour) |
| `area` | `game`, `sim`, `rule`, `crash`, `report`, `replay`, `save`, `ui`, `godot`, later `ai` |
| `data` | Anything extra, as JSON |

Godot's own errors (including C# exceptions Godot catches) are copied in under area `godot`.

Search it with the headless runner:

```
Lightlag.Headless logs data\logs --level warn
Lightlag.Headless logs data\logs --area rule --days 30-60
Lightlag.Headless logs data\nightly\2026-11-10_013000_nightly\log.jsonl --seed 4711 --text arrived
```

## Rule checks

Things that should never happen (`src/Lightlag.Sim/Logging/Rules.cs`), for example a position that
is not a number, two things with the same id, or a planet drifting off its orbit. They run once a
game day in normal play and every tick in headless runs.

- In the game a broken rule is logged as an error (area `rule`) and play carries on. A rule that
  keeps breaking is logged 5 times, then only counted, so it cannot flood the log.
- In headless runs and tests ("strict") a broken rule stops that game with a crash report.

Whenever a bug turns up that a check could have caught early, add a rule.

## Crash reports

When the simulation or the UI throws, the game writes `data/crashes/<time>_crash/` and shows where:

| File | What |
|---|---|
| `README.md` | What happened, build, seed, game time, how to replay |
| `error.txt` | The full exception and stack trace |
| `save.json` | The game at that moment |
| `replay.json` | The whole game so far |
| `log-tail.jsonl` | The last 500 log lines |

Ctrl+Shift+F12 in game causes a test crash, to check this end to end.

## F8 player reports

F8 on any screen opens a note box. Saving writes `data/reports/<time>_report/` with the note, a
screenshot and the same save, replay and log files.

## Replays

Every change to a game goes through a command (`src/Lightlag.Sim/Core/Commands.cs`), and all
randomness comes from the seeded `SimRandom`. A replay is the scenario, the seed and the list of
commands with the tick each took effect, plus a hash of the whole game state once per game day.

```
Lightlag.Headless replay data\crashes\<folder>\replay.json
```

plays it back, checks every daily hash, and reports either "matched", "drifted at day N" (something
is not deterministic: a bug to fix) or "reproduced a crash".

Rules that keep replays exact:

- Never use `System.Random`, `DateTime.Now` or anything else outside the seed inside the simulation.
- Never change the world from the UI; issue a command.
- Never iterate a `HashSet` or `Dictionary` where the order changes the result.
- Never call `Math.Sin`, `Math.Cos`, `Math.Pow` and friends in the simulation: they come from the
  operating system and differ in the last digit between Windows and Linux. Use `DetMath`. A test
  fails if one creeps in. With that, a replay recorded on one machine plays back exactly on another.

## Saves

Saves are JSON (`data/saves/`) and include the replay so far. F5 quick save, F9 quick load, an
autosave every 5 minutes of unpaused play and on quit (the last 5 are kept). Saves are written to a temp file
first, so a crash mid-save never corrupts one.
