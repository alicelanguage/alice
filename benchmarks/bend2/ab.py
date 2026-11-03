#!/usr/bin/env python3
"""Interleaved cold-process A/B on one serial core pair (perf2, benchmarks/README.md R0).

Every round runs each configuration once and the order alternates (A B C, C B A, ...), so drifts in the load of a
shared box hit all configurations alike; round 0 is a warm-up (page cache) and is not recorded. hyperfine runs all
runs of one command before the next, which on a busy box can move a ratio by more than the effect measured.

  CPUS=11,27 python3 ab.py ROUNDS cfg1,cfg2,... name1 name2 ...

Configuration `c` runs the C twin c/<name>_serial; any other runs $BIN/<cfg>/<name>.dll with ALICE_PAR=0 (build
each with the switch under test, e.g. `ALICE_NO_CACHE=1 ALICE_NO_AO=loop alice build kmeans.al -o $BIN/noloop/kmeans.dll`).
Results (wall ms per run) accumulate in $OUT (default results/ab.json); one line per workload is printed with the
median, range and each configuration's median over the first one, plus every checksum seen.
"""
import json, os, statistics, subprocess, sys, time

here = os.path.dirname(os.path.abspath(__file__))
cpus = os.environ.get('CPUS', '15,31')
bin_dir = os.environ.get('BIN', '/tmp/perf2-bench')
store = os.environ.get('OUT', os.path.join(here, 'results', 'ab.json'))
rounds, cfgs, names = int(sys.argv[1]), sys.argv[2].split(','), sys.argv[3:]
allres = json.load(open(store)) if os.path.exists(store) else {}
for n in names:
    res, sums = {c: [] for c in cfgs}, set()
    for r in range(rounds + 1):
        for c in (cfgs if r % 2 == 0 else cfgs[::-1]):
            cmd = ['taskset', '-c', cpus] + ([os.path.join(here, 'c', f'{n}_serial')] if c == 'c' else
                   ['env', 'ALICE_PAR=0', 'dotnet', os.path.join(bin_dir, c, f'{n}.dll')])
            t0 = time.perf_counter()
            p = subprocess.run(cmd, capture_output=True, text=True)
            dt = (time.perf_counter() - t0) * 1000
            sums.add((p.stdout.strip().splitlines() or ['?'])[-1])
            if r:
                res[c].append(dt)
    allres[n] = res
    json.dump(allres, open(store, 'w'))
    base = statistics.median(res[cfgs[0]])
    line = ' '.join(f"{c}={statistics.median(v):.0f}[{min(v):.0f}-{max(v):.0f}]" for c, v in res.items())
    line += ' | ' + ' '.join(f"{c}/{cfgs[0]}={statistics.median(v) / base:.3f}" for c, v in list(res.items())[1:])
    print(n, line, '| checksums', ','.join(sorted(sums)), flush=True)
