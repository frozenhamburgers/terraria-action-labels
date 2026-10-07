# terraria-action-labels

A patch that turns a Terraria boss fight (Eye of Cthulhu) into a seeded, resettable environment for
recording and evaluating agents, plus a MLP policy cloned from my human gameplay to show it working end to end.

Single-player only. The repository contains no game files: the patch is published as patcher code that
you run against a copy of the game.

## Part 1: Patch & Environment

Two calls injected into the game's update loop via Mono.Cecil provide the following:

- **State and action log:** Every tick is written as one JSON line:
    - *Held keys:* every input trigger the game knows, read after keybinds are applied
    - *Aim:* the cursor relative to the player in world pixels
    - *State:* player position, velocity, life and flight time; the boss's position, velocity, life and
      phase; the three nearest minions.
- **Environment control.** Resets to a fixed arena, loadout and RNG seed and spawns the boss.
  Actions can be injected per tick, and recorded fights can be reproduced exactly via replay.
- **Evaluation harness.** Runs N seeded episodes of any agent, at up to several times real speed, and
  reports win rate, survival time, damage dealt and life lost.

`src/LiveLog` follows the newest log and prints each tick as it's written (`--changes` prints only ticks
where the input changed), started with the patched game.

## Part 2: Demonstration

A small MLP trained in PyTorch on about 30 minutes of recorded fights, then run through the
harness. Forward pass is written in C# inside the game.

## Results
### Kite Algorithm - 100 Episodes

| Mode | Episodes | Win Rate | Survival (s) | Damage | Life Lost |
|---|---:|---:|---:|---:|---:|
| kite | 100 | 1.00 | 41.92 | 1.00 | 0.52 |

### The MLP

A behavior cloning policy: given the recent states of a fight, it predicts the action the cloned
player took next. Trained by `analysis/train.py`, run in game by `MlpAgent.cs`.

**Inputs (112).** The states after the last 4 steps, 5 ticks apart (1, 6, 11 and 16 ticks back),
28 features each:

| Part | Features |
|---|---|
| Player (6) | position relative to the arena's middle and lowest platform, velocity, life fraction, wing time left |
| Boss (7) | present flag, offset from the player, velocity, life fraction, phase |
| 3 nearest servants (5 each) | present flag, offset from the player, velocity |

Positions are divided by 1000 px and velocities by 10 px/tick, so most features fall in -1 to 1. Missing boss or servant is all zeros.

**Outputs (20).** 4 button logits (Left, Right, Jump, MouseLeft) and 16 aim logits, one per
direction around the player (22.5° apart, 200 px out). The model holds a button when its
probability is above 0.5, and aims in the most likely direction.

**Network.** Two hidden layers of 128 with ReLU (112 -> 128 -> 128 -> 20, about 33k parameters).

**Training.** Loss is binary cross-entropy for each button + cross-entropy for the aim. Adam
(learning rate 1e-3), batches of 256. 20% of episodes are held out for validation, since
neighbouring ticks are nearly identical, and the epoch with the lowest validation loss is exported.

**In game.** Weights are exported as text with a test input. GameHook runs an identical forward pass in
C#.

### MLP - Trained on 100 Kite Episodes for 30 Epochs, Evaluated on 30 Episodes
**Best validation loss:** 0.062

| Mode | Episodes | Win Rate | Survival (s) | Damage | Life Lost |
|---|---:|---:|---:|---:|---:|
| mlp | 30 | 1.00 | 44.33 | 1.00 | 0.50 |

### Human Play - 41 Episodes

| Mode | Episodes | Win Rate | Survival (s) | Damage | Life Lost |
|---|---:|---:|---:|---:|---:|
| play | 41 | 1.00 | 43.86 | 1.00 | 0.02 |

### MLP - Trained on 41 Human Play Episodes for 30 Epochs, Evaluated on 50 Episodes

This model displayed behaviors such as aiming for minions, as well as ducking under and jumping over the boss.
However, behaviors unique to Human Play such as dashes and flying were missing.

**Best validation loss:** 0.585


| Mode | Episodes | Win Rate | Survival (s) | Damage | Life Lost |
|---|---:|---:|---:|---:|---:|
| mlp | 50 | 0.90 | 55.55 | 0.97 | 0.71 |

### New MLP - Trained on 41 Human Play Episodes for 30 Epochs, Evaluated on 50 Episodes

Same data and network as above, with three changes aimed at the missing behaviors:

- **Button history as input:** Besides the last 4 states, each input holds the buttons pressed on
  each of the last 15 steps (`ACTIONS` in `train.py`). From states alone the model can't tell that it
  just tapped a direction (for a dash) or is in the middle of a flight, so it can't complete multi-tick patterns.
- **Sampled buttons:** Each tick a button is pressed with the probability the model gives it, instead
  of whenever that probability is above 0.5. The model learns a small chance per tick of starting or
  releasing a press, which a 0.5 cutoff never acts on. With the history added, the cutoff left it
  standing still at the start and holding one direction once moving. The samples come from an RNG
  seeded per episode, so episodes are still reproducible.
- **Down as an output**, for dropping through platforms.

So the network is 187 → 128 → 128 → 21: 112 state inputs plus 15 steps × 5 buttons, and 5 button
outputs plus 16 aim outputs. The weights trained with this new model are labeled "new."

Validation loss isn't directly comparable to the previous model's, since this one predicts one more
button and sees its own past buttons.

Visually, much closer to Human Play behavior, weaving over and under the boss,
and utilizing both dashes and extended flight.

**Best validation loss:** 0.474

| Mode | Episodes | Win Rate | Survival (s) | Damage | Life Lost |
|---|---:|---:|---:|---:|---:|
| mlp | 50 | 0.92 | 59.60 | 0.98 | 0.66 |

### New MLP - 41 Play Episodes for 30 Epochs, Evaluated on 50 Episodes + Extra Platforms

The results of the new MLP above may look similar, but were actually the result of
polarization. Once descending below the bottom platform, flying back up was very difficult
and required the player to stand on a specific elevation and use the entirety of their
wing duration. Thus, the model performed well if it never dropped below the lowest platform,
and poorly if it did. Adding another intermediate platform resulted in the below results, comparable
to the MLP trained on 100 Kite episodes.

| Mode | Episodes | Win Rate | Survival (s) | Damage | Life Lost |
|---|---:|---:|---:|---:|---:|
| mlp | 50 | 0.98 | 54.30 | 1.00 | 0.51 |

# Installation

## Requirements

- Windows, with Terraria 1.4.5.8 installed. The patcher refuses any other version.
- .NET 10 SDK (x64).
- Python 3.10+ with `pip install -r analysis/requirements.txt`, for analysis and training.

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

Launch `Terraria.exe` directly from that folder. GameHook writes one line per
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
- **F7** to run the keep-distance baseline (`kite`) for `EvalEpisodes` episodes (set in `Env.cs`), back to back,
  seeds 0 to `EvalEpisodes` - 1, at `EvalSpeed` times normal speed. Leave the game alone until it prints "Evaluation done".
- **F9** to evaluate the trained policy (see below) the same way, on the same seeds.

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

## Training a policy

`analysis/train.py` clones an agent from its episodes in one or more logs (globs work): `--mode play` for your
F5 fights (the default), or a baseline such as `--mode kite`. It writes `GameHookPolicy.txt`:

```
python analysis/train.py "C:\path\to\your\Terraria copy\GameHookLogs\*.jsonl" --mode play --out "C:\path\to\your\Terraria copy\GameHookPolicy.txt"
```

With that file in the game folder, F9 evaluates the policy, as `mlp`.

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