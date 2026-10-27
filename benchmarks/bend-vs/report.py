#!/usr/bin/env python3
"""Summarize hyperfine JSON in results/ into results/summary.md (median ms, ratio to C, parallel speedup)."""
import json, pathlib, statistics

R = pathlib.Path(__file__).parent / "results"
LANGS = ["bend", "alice", "c", "rust", "csharp"]
WORK = ["pow2", "fib", "tree", "nqueens", "mandel", "collatz"]

def load(name):
    d = json.loads((R / f"{name}.json").read_text())["results"]
    return {r["command"]: (statistics.median(r["times"]) * 1000, max(r["times"]) * 1000) for r in d}

out = ["# Results (median ms of cold-process wall time; max in parentheses)", ""]
start = load("startup")
out += ["## Startup floor (`pow2 1`)", "", "| " + " | ".join(LANGS) + " |", "|" + "---:|" * len(LANGS),
        "| " + " | ".join(f"{start[l][0]:.1f}" for l in LANGS) + " |", ""]
for mode, title in (("1", "Serial, 1 core"), ("12", "Parallel, 12 threads")):
    out += [f"## {title}", "", "| workload | " + " | ".join(LANGS) + " | Bend/Alice |", "|---|" + "---:|" * (len(LANGS) + 1)]
    for w in WORK:
        m = load(w)
        cells = [f"{m[f'{l}/{mode}'][0]:.0f} ({m[f'{l}/{mode}'][1]:.0f})" for l in LANGS]
        ratio = m[f"bend/{mode}"][0] / m[f"alice/{mode}"][0]
        out.append(f"| {w} | " + " | ".join(cells) + f" | {ratio:.2f} |")
    out.append("")
out += ["## Parallel speedup (serial median / parallel median)", "", "| workload | " + " | ".join(LANGS) + " |", "|---|" + "---:|" * len(LANGS)]
for w in WORK:
    m = load(w)
    out.append(f"| {w} | " + " | ".join(f"{m[f'{l}/1'][0] / m[f'{l}/12'][0]:.1f}x" for l in LANGS) + " |")
out += ["", "## Startup-subtracted serial time (median - startup floor), ms", "", "| workload | " + " | ".join(LANGS) + " |", "|---|" + "---:|" * len(LANGS)]
for w in WORK:
    m = load(w)
    out.append(f"| {w} | " + " | ".join(f"{max(m[f'{l}/1'][0] - start[l][0], 0):.0f}" for l in LANGS) + " |")
rss = {}
for line in (R / "rss.tsv").read_text().split("\n"):
    if line:
        w, cfg, kb = line.split("\t"); rss[(w, cfg)] = int(kb) / 1024
for mode in ("1", "12"):
    out += ["", f"## Peak RSS, MB ({mode} thread{'s' if mode != '1' else ''})", "", "| workload | " + " | ".join(LANGS) + " |", "|---|" + "---:|" * len(LANGS)]
    for w in WORK:
        out.append(f"| {w} | " + " | ".join(f"{rss[(w, f'{l}/{mode}')]:.0f}" for l in LANGS) + " |")
(R / "summary.md").write_text("\n".join(out) + "\n")
print("\n".join(out))
