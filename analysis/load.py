"""Load a GameHook log into a pandas DataFrame"""
import json

import numpy as np
import pandas as pd

# force fields that need to be float as float
# pandas infers types per file, so a file where every value happens to be whole would get an int column
FLOAT_FIELDS = ["px", "py", "vx", "vy", "wing", "bx", "by", "bvx", "bvy", "bphase"] + [
    f"s{k}{suffix}" for k in range(3) for suffix in ("x", "y", "vx", "vy")
]


def load(path):
    """Returns (ticks, triggers, episodes): one row per tick, the trigger name for each bit of
    `held`, and one row per episode (mode, result, steps). Ticks have an `ep` column, the
    episode they belong to or -1. Fields a row doesn't have (e.g. boss fields with no boss) become NaN."""
    freq, triggers, rows, episodes = None, [], [], []
    ep = -1
    with open(path, encoding="utf-8") as f:
        for line in f:
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue  # e.g. the last line, cut off when the game closed
            if obj["ev"] == "tick":
                obj["ep"] = ep
                rows.append(obj)
            elif obj["ev"] == "reset":
                ep = len(episodes)
                episodes.append({"ep": ep, "mode": obj["mode"], "result": None, "steps": None})
            elif obj["ev"] == "end":
                episodes[ep].update(result=obj["result"], steps=obj["steps"])
                ep = -1
            elif obj["ev"] == "start":
                freq = obj["freq"]
            elif obj["ev"] == "triggers":
                triggers = obj["names"]

    ticks = pd.DataFrame.from_records(rows).drop(columns="ev")
    # held can use all 64 bits, since int64 would only flip over at past bit 62
    ticks["held"] = ticks["held"].astype(np.uint64)
    for name in FLOAT_FIELDS:
        if name in ticks:
            ticks[name] = ticks[name].astype(np.float64)
    ticks.insert(0, "t", (ticks["ts"] - ticks["ts"].iloc[0]) / freq)
    return ticks, triggers, pd.DataFrame(episodes, columns=["ep", "mode", "result", "steps"])
