"""Aggregate GCSTAT lines (median over rounds) into results.csv and draw one SVG chart per metric."""
import csv
import statistics
import sys
from collections import defaultdict
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

raw, out = Path(sys.argv[1]), Path(sys.argv[2])
FIELDS = ["ms", "bytes", "gen0", "gen1", "gen2", "pause_ms", "peak_bytes"]
runs = defaultdict(list)
checks = {}
for line in raw.read_text().splitlines():
    lang, mode, _, bench, *vals = line.split()
    runs[(bench, mode, lang)].append([float(v) for v in vals[:7]])
    checks.setdefault((bench, mode), set()).add(vals[7])

rows = []
for (bench, mode, lang), samples in sorted(runs.items()):
    med = [statistics.median(s[i] for s in samples) for i in range(len(FIELDS))]
    rows.append({"bench": bench, "gc": mode, "lang": lang, **dict(zip(FIELDS, med))})
bad = [k for k, v in checks.items() if len(v) != 1]
if bad:
    sys.exit(f"checksum mismatch: {bad}")

with open(out / "results.csv", "w", newline="") as f:
    w = csv.DictWriter(f, fieldnames=["bench", "gc", "lang", *FIELDS])
    w.writeheader()
    w.writerows(rows)

benches = ["rows", "scratch", "frames", "trees"]
value = {(r["bench"], r["gc"], r["lang"]): r for r in rows}
COLORS = {"alice": "#7c4dff", "alice-own": "#c2185b", "csharp": "#2e8b57"}
LABELS = {"alice": "Alice (inferred)", "alice-own": "Alice (explicit own)", "csharp": "C#"}
LANGS = list(COLORS)
W = 0.27
CHARTS = [
    ("time", "Median time per run (ms)", lambda r: r["ms"], False),
    ("allocated", "Allocated per run (MiB)", lambda r: r["bytes"] / 2**20, True),
    ("collections", "GC collections, 7 runs (gen0 + gen1 + gen2)", lambda r: r["gen0"] + r["gen1"] + r["gen2"], True),
    ("pause", "GC pause time, 7 runs (ms)", lambda r: r["pause_ms"], True),
    ("peak", "Peak working set (MiB)", lambda r: r["peak_bytes"] / 2**20, False),
]
for key, title, get, log in CHARTS:
    fig, axes = plt.subplots(1, 2, figsize=(13, 4.5), sharey=True)
    for ax, mode in zip(axes, ["workstation", "server"]):
        for j, lang in enumerate(LANGS):
            pts = [(i + (j - 1) * W, get(value[(b, mode, lang)])) for i, b in enumerate(benches) if (b, mode, lang) in value]
            if not pts:
                continue
            xs, ys = zip(*pts)
            bars = ax.bar(xs, [max(y, 1e-3) if log else y for y in ys], W, color=COLORS[lang], label=LABELS[lang])
            for bar, y in zip(bars, ys):
                ax.annotate(f"{y:.3g}", (bar.get_x() + bar.get_width() / 2, bar.get_height()),
                            ha="center", va="bottom", fontsize=8)
        ax.set_xticks(range(len(benches)), benches)
        ax.set_title(f"{mode} GC")
        if log:
            ax.set_yscale("log")
        ax.spines[["top", "right"]].set_visible(False)
    axes[0].legend(frameon=False)
    fig.suptitle(title + (" — log scale" if log else ""))
    fig.tight_layout()
    fig.savefig(out / f"{key}.svg")
    plt.close(fig)

with open(out / "results.md", "w") as f:
    f.write("| bench | GC | variant | ms | MiB/run | GCs (7 runs) | pause ms | peak MiB |\n")
    f.write("|---|---|---|---:|---:|---:|---:|---:|\n")
    for mode in ["workstation", "server"]:
        for b in benches:
            for lang in LANGS:
                if (r := value.get((b, mode, lang))) is None:
                    continue
                f.write(f"| {b} | {mode} | {LABELS[lang]} | {r['ms']:.1f} | {r['bytes'] / 2**20:.2f} "
                        f"| {int(r['gen0'] + r['gen1'] + r['gen2'])} | {r['pause_ms']:.1f} | {r['peak_bytes'] / 2**20:.0f} |\n")
print((out / "results.md").read_text())
