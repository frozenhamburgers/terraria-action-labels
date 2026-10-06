# terraria-action-labels

Goal: Measures how well player actions in Terraria can be labelled from outside the game, by comparing them against ground truth logged from inside it. 
Currently single-player only.

This repository contains patcher code  that you run against your own copy of the game.

## Requirements

- Windows, with Terraria 1.4.5.8 installed. The patcher refuses any other version.
- .NET 10 SDK (x64).

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
3. inserts a single call to `GameHook.Hook.OnTick()` at the start of
   `Terraria.Main.DoUpdate`
4. copies `GameHook.dll` into the game folder

Launch `Terraria.exe` directly from that folder. Currently, gameHook writes one line per
game tick to `GameHookLogs/gamehook-<timestamp>.jsonl` inside the game folder:

```
{"ev":"start","freq":10000000}
{"ev":"triggers","names":["MouseLeft","MouseRight","Up",...],"dropped":0}
{"ev":"tick","ts":123456789,"tick":4213,"held":65,"menu":0}
```

`ts` is a `Stopwatch` timestamp. Divide it by `freq` to get seconds.
`held` is a bitmask: bit *i* set means trigger `names[i]` was held on that
tick (65 = MouseLeft + Jump). `menu` is 1 on the main menu.

## Restore

```
src\Patcher\bin\Debug\net10.0\Patcher.exe restore "C:\path\to\your\Terraria copy"
```

This puts the original `Terraria.exe` back, then removes `GameHook.dll` &
the backup.
