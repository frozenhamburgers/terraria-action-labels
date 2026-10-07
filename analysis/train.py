"""Behavior cloning: trains a small MLP to predict an agent's actions from the states before them,
and exports it for GameHook's MlpAgent.

    python train.py <log> [<log> ...] [--mode play] [--out GameHookPolicy.txt]

Logs can be globs. Only finished episodes of --mode are used (play = your F5 fights; kite etc.
to clone the baseline). Copy the output into the game folder, then F9 evaluates it.
"""
import argparse
import glob

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

from load import load

BUTTONS = ["Left", "Right", "Jump", "Down", "MouseLeft"]  # Down drops through platforms
AIM_DIRECTIONS = 16  # Agents.AimDirections in GameHook
HISTORY = 4          # states per input
SPACING = 5          # ticks between them
ACTIONS = 15         # past ticks of buttons per input, so holds and double taps can be learned
HIDDEN = 128

# same scales as MlpAgent.cs
POSITION_SCALE = 1000.0
VELOCITY_SCALE = 10.0
WING_SCALE = 100.0
SERVANTS = 3


def frame_features(rows, ox, oy):
    """One row of features per tick, the same numbers as MlpAgent.Features(). Missing boss or
    servants are all zeros, including their 'present' flag."""
    px, py = rows["px"].values, rows["py"].values
    columns = [
        (px - ox) / POSITION_SCALE,
        (py - oy) / POSITION_SCALE,
        rows["vx"].values / VELOCITY_SCALE,
        rows["vy"].values / VELOCITY_SCALE,
        rows["hp"].values / rows["hpMax"].values,
        rows["wing"].values / WING_SCALE,
    ]

    def present(column):
        return rows[column].notna().values if column in rows else np.zeros(len(rows), bool)

    def part(column, value):
        return np.where(present(column), value, 0.0) if column in rows else np.zeros(len(rows))

    boss = present("bx")
    columns += [
        boss.astype(float),
        part("bx", (rows.get("bx", 0) - px) / POSITION_SCALE),
        part("by", (rows.get("by", 0) - py) / POSITION_SCALE),
        part("bvx", rows.get("bvx", 0) / VELOCITY_SCALE),
        part("bvy", rows.get("bvy", 0) / VELOCITY_SCALE),
        part("bhp", rows.get("bhp", 0) / rows.get("bhpMax", 1)),
        part("bphase", rows.get("bphase", 0) / 3),
    ]
    for k in range(SERVANTS):
        s = f"s{k}"
        columns += [
            present(s + "x").astype(float),
            part(s + "x", (rows.get(s + "x", 0) - px) / POSITION_SCALE),
            part(s + "y", (rows.get(s + "y", 0) - py) / POSITION_SCALE),
            part(s + "vx", rows.get(s + "vx", 0) / VELOCITY_SCALE),
            part(s + "vy", rows.get(s + "vy", 0) / VELOCITY_SCALE),
        ]
    return np.nan_to_num(np.stack([np.asarray(c, dtype=np.float64) for c in columns], axis=1)).astype(np.float32)


def episode_samples(rows, ox, oy, bits):
    """Inputs and targets for every step but the first. Row t holds step t's action and the state
    after it, so step t is predicted from rows t-1, t-1-SPACING, ... (clamped to row 0), followed
    by the buttons of steps t-1 to t-ACTIONS (none before the episode started)."""
    features = frame_features(rows, ox, oy)
    held = rows["held"].values.astype(np.uint64)
    all_buttons = np.stack([(held >> np.uint64(b)) & np.uint64(1) for b in bits], axis=1).astype(np.float32)
    all_buttons = np.concatenate([np.zeros((ACTIONS, len(bits)), np.float32), all_buttons])  # row t is at ACTIONS + t

    steps = np.arange(1, len(rows))
    x = np.concatenate(
        [features[np.maximum(steps - 1 - h * SPACING, 0)] for h in range(HISTORY)]
        + [all_buttons[ACTIONS + steps - k] for k in range(1, ACTIONS + 1)], axis=1)

    buttons = all_buttons[ACTIONS + steps]

    angle = np.arctan2(rows["ay"].values[steps], rows["ax"].values[steps])
    aim = np.round(angle / (2 * np.pi / AIM_DIRECTIONS)).astype(np.int64) % AIM_DIRECTIONS
    return x, buttons, aim


def load_episodes(paths, mode):
    """One (x, buttons, aim) per finished episode of this mode."""
    episodes = []
    for path in paths:
        ticks, triggers, eps = load(path)
        bits = [triggers.index(b) for b in BUTTONS]
        for _, ep in eps[(eps["mode"] == mode) & eps["result"].notna()].iterrows():
            rows = ticks[ticks["ep"] == ep["ep"]].reset_index(drop=True)
            ox, oy = ep["ox"], ep["oy"]
            if ox is None or np.isnan(ox):
                # logs from before the reset line had the arena origin: the player starts
                # 2 px right of the middle, standing on the floor (half of 42 px tall)
                ox, oy = rows["px"].iloc[0] - 2, rows["py"].iloc[0] + 21
            episodes.append(episode_samples(rows, ox, oy, bits))
    return episodes


def stack(episodes):
    x, buttons, aim = (np.concatenate(parts) for parts in zip(*episodes))
    return torch.from_numpy(x), torch.from_numpy(buttons), torch.from_numpy(aim)


def loss_fn(out, buttons, aim):
    return (F.binary_cross_entropy_with_logits(out[:, :len(BUTTONS)], buttons)
            + F.cross_entropy(out[:, len(BUTTONS):], aim))


def export(model, path, test_input):
    """Whitespace separated tokens, read by MlpAgent.Load. Weights are [out, in], row by row.
    Ends with one input and PyTorch's output for it, so the C# side can check its forward pass."""
    layers = [m for m in model if isinstance(m, nn.Linear)]
    with open(path, "w", encoding="utf-8") as f:
        f.write(f"history {HISTORY} {SPACING}\n")
        f.write(f"buttons {len(BUTTONS)} {' '.join(BUTTONS)}\n")
        f.write(f"actions {ACTIONS}\n")
        f.write(f"aims {AIM_DIRECTIONS}\n")
        f.write(f"layers {len(layers)}\n")
        for layer in layers:
            w = layer.weight.detach().numpy()
            f.write(f"{w.shape[0]} {w.shape[1]}\n")
            for row in w:
                f.write(" ".join(f"{v:.9g}" for v in row) + "\n")
            f.write(" ".join(f"{v:.9g}" for v in layer.bias.detach().numpy()) + "\n")
        with torch.no_grad():
            test_output = model(test_input[None])[0].numpy()
        f.write(f"test {len(test_input)}\n" + " ".join(f"{v:.9g}" for v in test_input.numpy()) + "\n")
        f.write(" ".join(f"{v:.9g}" for v in test_output) + "\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("logs", nargs="+")
    parser.add_argument("--mode", default="play")
    parser.add_argument("--out", default="GameHookPolicy.txt")
    parser.add_argument("--epochs", type=int, default=30)
    args = parser.parse_args()

    torch.manual_seed(0)
    rng = np.random.default_rng(0)
    paths = [p for pattern in args.logs for p in sorted(glob.glob(pattern))]
    episodes = load_episodes(paths, args.mode)
    if len(episodes) < 2:
        raise SystemExit(f"need at least 2 finished '{args.mode}' episodes, found {len(episodes)}")

    # whole episodes go to validation: neighbouring ticks are nearly identical, so a split by
    # tick would let validation look like training data
    order = rng.permutation(len(episodes))
    n_val = max(1, len(episodes) // 5)
    train = stack([episodes[i] for i in order[n_val:]])
    val = stack([episodes[i] for i in order[:n_val]])
    print(f"{len(episodes) - n_val} training episodes ({len(train[0])} steps), {n_val} validation ({len(val[0])} steps)")
    # accuracy of always guessing a button's more common value, which the model should beat
    rates = val[1].mean(0).tolist()
    print(f"{'baseline':26}" + " ".join(f"{b} {max(r, 1 - r):.2f}" for b, r in zip(BUTTONS, rates)))

    inputs = train[0].shape[1]
    model = nn.Sequential(
        nn.Linear(inputs, HIDDEN), nn.ReLU(),
        nn.Linear(HIDDEN, HIDDEN), nn.ReLU(),
        nn.Linear(HIDDEN, len(BUTTONS) + AIM_DIRECTIONS),
    )
    optimizer = torch.optim.Adam(model.parameters(), lr=1e-3)

    best, best_state = float("inf"), None
    for epoch in range(args.epochs):
        model.train()
        for batch in torch.randperm(len(train[0])).split(256):
            loss = loss_fn(model(train[0][batch]), train[1][batch], train[2][batch])
            optimizer.zero_grad()
            loss.backward()
            optimizer.step()

        model.eval()
        with torch.no_grad():
            out = model(val[0])
            val_loss = loss_fn(out, val[1], val[2]).item()
            button_acc = ((out[:, :len(BUTTONS)] > 0).float() == val[1]).float().mean(0)
            aim_acc = (out[:, len(BUTTONS):].argmax(1) == val[2]).float().mean().item()
        accs = " ".join(f"{b} {a:.2f}" for b, a in zip(BUTTONS, button_acc.tolist()))
        print(f"epoch {epoch + 1:2d}  val loss {val_loss:.3f}  {accs}  aim {aim_acc:.2f}")
        # the exported weights are the epoch that did best on validation
        if val_loss < best:
            best, best_state = val_loss, {k: v.clone() for k, v in model.state_dict().items()}

    model.load_state_dict(best_state)
    model.eval()
    export(model, args.out, val[0][0])
    print(f"best val loss {best:.3f}, wrote {args.out}")


if __name__ == "__main__":
    main()
