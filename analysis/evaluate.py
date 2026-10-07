"""Scores every finished episode in a log, grouped by who played it (play, idle, random, kite, ...).

    python evaluate.py <log>

win rate:  share of episodes where the Eye of Cthulhu died
survival:  mean seconds until the episode ended (death, win or timeout)
damage:    mean damage dealt to the Eye, as a share of its max life
"""
import sys

import pandas as pd

from load import load

TICKS_PER_SECOND = 60

ticks, _, episodes = load(sys.argv[1])
episodes = episodes[episodes["result"].notna() & (episodes["mode"] != "replay")].copy()
if episodes.empty:
    sys.exit("no finished episodes in this log")


def damage_share(ep):
    rows = ticks[ticks["ep"] == ep]
    boss = rows["bhp"].dropna()
    if boss.empty:
        return 0.0
    # rows after a win have no boss fields, so the last row with them still shows a little life
    final = 0 if episodes.loc[episodes["ep"] == ep, "result"].item() == "win" else boss.iloc[-1]
    return 1 - final / rows["bhpMax"].dropna().iloc[0]


episodes["win"] = episodes["result"] == "win"
episodes["survival"] = episodes["steps"] / TICKS_PER_SECOND
episodes["damage"] = [damage_share(ep) for ep in episodes["ep"]]

report = episodes.groupby("mode", sort=False).agg(
    episodes=("ep", "size"),
    win_rate=("win", "mean"),
    survival_s=("survival", "mean"),
    damage=("damage", "mean"),
)
pd.set_option("display.float_format", "{:.2f}".format)
print(report)
