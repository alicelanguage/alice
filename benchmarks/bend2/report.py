#!/usr/bin/env python3
"""Summarize results/*.json (hyperfine, schema 1 or 2) into results/summary.md.

Per workload and mode: median ms, max ms, peak RSS, Alice/C ratio, parallel speedup, checksum agreement.
Bend's pinned numbers (Apple M4 Max, a different machine) are shown as a separate reference column
and only compared through machine-independent ratios (X / C on the same machine).
"""
import json, pathlib, re, statistics

HERE = pathlib.Path(__file__).parent
R = HERE / "results"
PIN = HERE / "bend_pin_apple_m4_max.txt"
SKIP = {"startup"}


def load(path):
    """command name -> (median s, max s, median peak RSS MB or None)"""
    d = json.loads(path.read_text())
    out = {}
    for r in d["results"]:
        if "measurements" in r:  # hyperfine 2 (schema_version 2)
            ms = r["measurements"]
            t = [m["time_wall_clock"]["value"] for m in ms]
            rss = [m["memory_peak_resident"]["value"] / 2**20 for m in ms if "memory_peak_resident" in m]
        else:  # hyperfine 1
            t = r["times"]
            rss = [x / 2**20 for x in r.get("memory_usage_byte", [])]
        out[r.get("name") or r["command"]] = (statistics.median(t), max(t), statistics.median(rss) if rss else None)
    return out


def pins():
    """bench -> {SEQ-CPU, PAR-CPU, PAR-GPU: (s, MB)} and C, TS, Lean: s"""
    if not PIN.exists():
        return {}
    rows = {}
    for line in PIN.read_text().splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) != 7 or not re.match(r"^\d", cells[1]):
            continue
        def tm(c):
            m = re.match(r"([\d.]+)s(?:\s+([\d.]+)M)?", c)
            return (float(m.group(1)), float(m.group(2)) if m.group(2) else None)
        rows[cells[0]] = {"SEQ": tm(cells[1]), "PAR": tm(cells[2]), "GPU": tm(cells[3]), "C": tm(cells[4])[0]}
    return rows


names = sorted(p.stem for p in R.glob("*.json") if p.stem not in SKIP)
data = {n: load(R / f"{n}.json") for n in names}
bend = pins()
cores = (R / "cores.txt").read_text().strip() if (R / "cores.txt").exists() else "N"
sums = {}
if (R / "checksums.tsv").exists():
    for line in (R / "checksums.tsv").read_text().splitlines():
        if line:
            n, cfg, v = line.split("\t")
            sums.setdefault(n, {})[cfg] = v


def ms(x):
    return f"{x * 1000:.0f}"


def mb(x):
    return "-" if x is None else f"{x:.0f}"


out = ["# Bend 2 runtime suite: results", "",
       f"Median of cold-process wall time in ms (max in parentheses), peak RSS in MB (median). Serial = 1 core "
       f"(`taskset`, `DOTNET_PROCESSOR_COUNT=1` for Alice), parallel = all {cores} cores. "
       "Bend columns are the pinned numbers of bend/bench/runtime/_pin_/apple_m4_max.txt: **a different machine "
       "(Apple M4 Max, 16 cores)**, for reference only; compare them through the X/C ratios, never directly.", ""]

if (R / "startup.json").exists():
    st = load(R / "startup.json")
    first = (R / "startup.txt").read_text().strip() if (R / "startup.txt").exists() else "bfs"
    out += [f"Alice startup floor (`{first} 0`, smallest input): serial {ms(st['alice/1'][0])} ms, parallel {ms(st['alice/N'][0])} ms.", ""]

for mode, title, bk in (("1", "Serial, 1 core", "SEQ"), ("N", f"Parallel, {cores} cores", "PAR")):
    out += [f"## {title}", "",
            "| workload | Alice ms | C ms | Alice / C | Alice RSS | C RSS | Bend M4 ms (different machine) | Bend / C on M4 |",
            "|---|---:|---:|---:|---:|---:|---:|---:|"]
    for n in names:
        m = data[n]
        a, c = m[f"alice/{mode}"], m[f"c/{mode}"]
        # C on the M4 is Bend's serial C twin; the parallel ratio is Bend PAR over that serial C
        b = bend.get(n)
        bcell = f"{b[bk][0] * 1000:.0f}" if b else "-"
        bratio = f"{b[bk][0] / b['C']:.2f}" if b else "-"
        out.append(f"| {n} | {ms(a[0])} ({ms(a[1])}) | {ms(c[0])} ({ms(c[1])}) | {a[0] / c[0]:.2f} | {mb(a[2])} | {mb(c[2])} | {bcell} | {bratio} |")
    out.append("")

out += ["## Parallel speedup (serial median / parallel median)", "",
        f"| workload | Alice | C (OpenMP) | Bend on M4 (16 cores, different machine) |", "|---|---:|---:|---:|"]
for n in names:
    m = data[n]
    b = bend.get(n)
    bs = f"{b['SEQ'][0] / b['PAR'][0]:.1f}x" if b else "-"
    out.append(f"| {n} | {m['alice/1'][0] / m['alice/N'][0]:.1f}x | {m['c/1'][0] / m['c/N'][0]:.1f}x | {bs} |")

out += ["", "## Alice against Bend, machine-independent", "",
        "Each side divided by the serial C twin on its own machine (lower is better). "
        "`Alice ahead` means Alice/C here is below Bend/C on the M4.", "",
        "| workload | serial: Alice / C | serial: Bend / C | parallel: Alice / C serial | parallel: Bend / C serial | serial | parallel |",
        "|---|---:|---:|---:|---:|---|---|"]
for n in names:
    m = data[n]
    b = bend.get(n)
    if not b:
        continue
    a1 = m["alice/1"][0] / m["c/1"][0]
    aN = m["alice/N"][0] / m["c/1"][0]
    b1 = b["SEQ"][0] / b["C"]
    bN = b["PAR"][0] / b["C"]
    out.append(f"| {n} | {a1:.2f} | {b1:.2f} | {aN:.3f} | {bN:.3f} | {'Alice ahead' if a1 < b1 else 'Bend ahead'} | "
               f"{'Alice ahead' if aN < bN else 'Bend ahead'} |")

out += ["", "## Checksums", "", "| workload | Alice serial | Alice parallel | C serial | C parallel | match |", "|---|---|---|---|---|---|"]
for n in names:
    s = sums.get(n, {})
    vals = [s.get(k, "?") for k in ("alice/1", "alice/N", "c/1", "c/N")]
    ok = "yes" if len(set(vals)) == 1 and "?" not in vals else "**NO**"
    out.append(f"| {n} | " + " | ".join(vals) + f" | {ok} |")

(R / "summary.md").write_text("\n".join(out) + "\n")
print("\n".join(out))
