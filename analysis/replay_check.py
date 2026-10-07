"""Checks that a replay matched the fight it replayed: compares the last "play" episode in a log
with the "replay" episode after it, row by row, and prints the first row where they differ.

    python replay_check.py <log>
"""
import sys

import numpy as np

from load import load

# state that a replay must reproduce exactly
COMPARED = ["px", "py", "vx", "vy", "hp", "bx", "by", "bvx", "bvy", "bhp", "sn"]

ticks, _, episodes = load(sys.argv[1])
played = episodes[episodes["mode"] == "play"]
if played.empty:
    sys.exit("no played episode in this log (press F5 in game)")
play = played.iloc[-1]
replays = episodes[(episodes["mode"] == "replay") & (episodes["ep"] > play["ep"])]
if replays.empty:
    sys.exit("no replay after the last played episode (press F6 in game)")
replay = replays.iloc[0]
print(f"played: {play['result']} after {play['steps']} steps; replay: {replay['result']} after {replay['steps']} steps")

a = ticks[ticks["ep"] == play["ep"]].reset_index(drop=True)
b = ticks[ticks["ep"] == replay["ep"]].reset_index(drop=True)
n = min(len(a), len(b))
columns = [c for c in COMPARED if c in a and c in b]
# NaN == NaN counts as equal: both have no boss
same = (a[columns].iloc[:n].values == b[columns].iloc[:n].values) | (a[columns].iloc[:n].isna().values & b[columns].iloc[:n].isna().values)
bad = np.flatnonzero(~same.all(axis=1))
if len(bad) == 0 and len(a) == len(b):
    print(f"identical for all {n} rows")
elif len(bad) == 0:
    print(f"identical for {n} rows, but one episode has {abs(len(a) - len(b))} more")
else:
    row = bad[0]
    print(f"first difference at row {row} of {n}:")
    print(a.loc[row, columns].to_frame("played").join(b.loc[row, columns].to_frame("replay")))
