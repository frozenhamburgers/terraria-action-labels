# terraria-action-labels

Goal: Measures how well player actions in Terraria can be labelled from outside the game, by comparing them against ground truth logged from inside it. 
Currently single-player only.

This repository contains patcher code  that you run against your own copy of the game.

## Requirements

- Windows, with Terraria 1.4.5.8 installed. The patcher refuses any other version.
- .NET 10 SDK (x64).
- Python 3.10+ with `pip install -r analysis/requirements.txt`, for loading logs.

## Setup

**Make sure to patch a copy of the game, not your Steam install.** Copy the Terraria folder
somewhere else.

Then create `local.props` at the repo root so the build can find the game.
This file is gitignored.

```xml
<Project>
  <PropertyGroup>
    <TerrariaDir>C:\path\to\your\Terraria copy</TerrariaDir>
  </PropertyGroup>
</Project>
```

## Build

```
dotnet build
```

This builds both projects:

- `GameHook.dll` (net40) is the patched code the game will call. It is compiled against
  `Terraria.exe` from `TerrariaDir`.
- `Patcher.exe` (net10.0) injects the call. GameHook is built first and
  copied next to it.

Output goes to `src/Patcher/bin/Debug/net10.0/`.

## Patch

```
src\Patcher\bin\Debug\net10.0\Patcher.exe patch "C:\path\to\your\Terraria copy"
```

The patcher:

1. backs up `Terraria.exe` to `Terraria.exe.orig` the first time it runs
2. always patches from that backup, so running it again is safe
3. inserts a call to `GameHook.Hook.OnTick()` at the start of
   `Terraria.Main.DoUpdate`, and one to `GameHook.Hook.OnWorldUpdate()` at the
   start of `Terraria.Main.DoUpdateInWorld`
4. copies `GameHook.dll` into the game folder

Launch `Terraria.exe` directly from that folder. Currently, gameHook writes one line per
game tick to `GameHookLogs/gamehook-<timestamp>.jsonl` inside the game folder:

```
{"ev":"start","freq":10000000}
{"ev":"triggers","names":["MouseLeft","MouseRight","Up",...],"dropped":0}
{"ev":"tick","ts":123456789,"tick":4213,"held":65,"menu":0,"ax":182,"ay":-40,"slot":0,"px":33605.5,"py":6531,"vx":-3.25,"vy":0,"hp":400,"hpMax":400,"wing":0,"rocket":7,"dead":0,"bx":33900,"by":6300,"bvx":1.5,"bvy":-2,"bhp":2800,"bhpMax":2800,"bphase":0,"sn":1,"s0x":33700,"s0y":6450,"s0vx":2.1,"s0vy":0.4}
```

`ts` is a `Stopwatch` timestamp. Divide it by `freq` to get seconds.
`held` is a bitmask: bit *i* set means trigger `names[i]` was held on that
tick (65 = MouseLeft + Jump). `ax`/`ay` is the cursor relative to the player in
world pixels, `slot` the inventory index of the item in hand, and `menu` is 1 on
the main menu. The rest is the player's state after the tick: position,
velocity, health, flight time and whether or not they're dead, then the Eye of
Cthulhu (only while it's alive) and the nearest Servants of Cthulhu.

## Collecting Data via Episodes

**Use a throwaway character and world.** The first reset in a session changes both, and the game saves them.

In a world, press:

- **F5** to start a fight you play. The first press builds arena and gear, and every press restores that snapshot, sets the time to night and spawns the Eye of Cthulhu.
- **F6** to reset the same way and replay the actions of your last F5 fight. Don't touch the keyboard or
  mouse while it runs.
- **F7** to run the evaluation: the idle, random and keep-distance (`kite`) baselines play 10 episodes each,
  back to back, with the same 10 seeds. Leave the game alone until it prints "Evaluation done".

Each episode has a seed for the game's random numbers. F5 picks a new one, and F6 reuses the one it replays.

An episode ends on a win, your death, the Eye leaving, or after 3 minutes. The log marks it with
`reset` and `end` lines. To check that a replay matched the fight:

```
python analysis/replay_check.py "C:\path\to\your\Terraria copy\GameHookLogs\gamehook-<timestamp>.jsonl"
```

To score every episode in a log by who played it (win rate, mean survival time, mean damage dealt):

```
python analysis/evaluate.py "C:\path\to\your\Terraria copy\GameHookLogs\gamehook-<timestamp>.jsonl"
```

## Loading logs

`analysis/load.py` turns a log into a DataFrame, one row per tick, with a `t` column in seconds:

```python
from load import load
ticks, triggers, episodes = load(r"C:\path\to\your\Terraria copy\GameHookLogs\gamehook-<timestamp>.jsonl")
```

Bit *i* of `held` is `triggers[i]`. `ticks["ep"]` is the episode a row belongs to (-1 for none),
and `episodes` has each episode's mode, result and length.

## Restore

```
src\Patcher\bin\Debug\net10.0\Patcher.exe restore "C:\path\to\your\Terraria copy"
```

This puts the original `Terraria.exe` back, then removes `GameHook.dll` &
the backup.
