# Lightlag

An original 4X strategy game set in one solar system. Real time with pause, on a 2D map of orbits
and gravity wells, with hard sci-fi ships. Built with Godot 4 (.NET / C#) for Windows.

Status: **Milestone 0, foundations.** The game opens to a title screen and a test solar system where
two placeholder ships per side can be ordered around. The point of M0 is the plumbing every later
milestone relies on: the simulation core, logging, crash reports, replays and one-click builds.

## Playing (Windows)

One-time setup, from a clone of this repo:

```powershell
powershell -ExecutionPolicy Bypass -File tools\Install-Shortcuts.ps1
```

That creates a `play\` folder in the clone and two desktop shortcuts:

- **Lightlag**: downloads the newest build if there is one, then starts the game.
- **Lightlag (previous build)**: swaps back to the build before, if the new one is broken.

Every merge to `main` builds a new release automatically (see `.github/workflows/build.yml`).
The build number shows on the title screen and in-game.

| Key | In game |
|---|---|
| **F8** | Report a problem: type a note; a screenshot, save, replay and log are attached |
| Space, 1-4 | Pause, game speed |
| F5 / F9 | Quick save / quick load |
| Wheel, drag | Zoom, pan |
| Left / right click | Select a ship / send it somewhere |
| Esc | Autosave and back to the title |
| Ctrl+Shift+F12 | Test crash (checks that crash reports work) |

## Where things go

Everything the game writes lives in `play\data\`, separate from the builds, so updating or rolling
back never touches it:

| Folder | What |
|---|---|
| `logs\` | One `.jsonl` log per session; every line has build, seed, game day and area |
| `reports\` | F8 reports: note, screenshot, save, replay, log tail |
| `crashes\` | Crash reports: error, save, replay, log tail, and a README saying how to replay |
| `saves\` | Quick save, manual saves and the last 5 autosaves |
| `nightly\` | Overnight runs; `latest-summary.md` is the morning summary, `latest-battles.md` the battle sweep |

See [docs/LOGGING.md](docs/LOGGING.md) for details, and [docs/BUILDS.md](docs/BUILDS.md) for how
builds, updates and overnight runs work.

## Overnight runs

From the play folder, once:

```powershell
powershell -ExecutionPolicy Bypass -File Register-Nightly.ps1
```

Each night it updates to the newest build and plays a batch of headless games with every rule
check switched to strict. The title screen shows last night's result.

## Code layout

| Path | What |
|---|---|
| `src/Lightlag.Sim` | The simulation: world state, commands, rules, logging, saves, replays. No Godot. |
| `src/Lightlag.Headless` | Command-line runner: overnight batches, replay checks, log search |
| `tests/Lightlag.Sim.Tests` | Tests for the simulation (run in CI on every push) |
| `game/` | The Godot project: screens, drawing and input only |
| `tools/` | Windows launcher, shortcut setup and nightly scripts |

Developing: install the .NET 8 SDK and the Godot 4.7 .NET editor, then

```bash
dotnet test Lightlag.sln                                        # simulation tests
dotnet run --project src/Lightlag.Headless -- batch --games 10   # quick headless batch
dotnet run --project src/Lightlag.Headless -- battles --repeats 1 # quick battle sweep
```

and open `game/` in Godot.
