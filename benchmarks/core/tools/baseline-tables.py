# Pivots a RESULT file into the tables of results/baseline-forge.md: python3 tools/baseline-tables.py results/baseline-forge.raw.txt
import sys, collections
raw = sys.argv[1]
lines = open(raw).read().splitlines()
rows = [l.split() for l in lines[1:] if l.startswith('RESULT')]
R = {}
for _, eng, case, inp, ms, mbs, alloc, chk in rows:
    R[(case, inp, eng)] = (float(ms), float(mbs) / 1000, alloc, chk)
def table(cases, inputs, engines, val, hdr):
    out = ['| ' + hdr + ' | ' + ' | '.join(engines) + ' |', '|---|' + '---:|' * len(engines)]
    for case in cases:
        for inp in inputs:
            cells = [val(R[(case, inp, e)]) if (case, inp, e) in R else '' for e in engines]
            if any(cells): out.append(f'| {case} {inp} | ' + ' | '.join(cells) + ' |')
    return '\n'.join(out)
gb = lambda r: f'{r[1]:.3f}'
files = ['twitter', 'canada', 'citm_catalog', 'github_events', 'gsoc-2018', 'mesh']
print(table(['json-dom'], files, ['stj/JsonDocument', 'stj/JsonNode', 'serde_json/value', 'simd-json/borrowed', 'simdjson/dom'], gb, 'DOM parse, GB/s'))
print()
print(table(['json-walk'], files, ['stj/Utf8JsonReader', 'simdjson/ondemand-walk'], gb, 'Full token walk, GB/s'))
print()
print(table(['json-typed', 'json-serialize'], files[:3], ['stj/sourcegen', 'serde_json/typed', 'simd-json/typed'], gb, 'Typed, GB/s'))
print()
print('Allocated bytes per call (.NET): ' + ', '.join(f'{c} {i} {e} {R[(c,i,e)][2]}' for (c, i, e) in R if e.startswith('stj') and c in ('json-dom','json-typed','json-serialize') and i in ('twitter','canada','citm_catalog')) + '.')
print()
ms = lambda r: f'{r[0]:.0f}'
print(table(['regex-redux'], ['fasta-5M'], ['dotnet/interpreted', 'dotnet/compiled', 'dotnet/nonbacktracking', 'rust/regex'], ms, 'regex-redux, ms (single-threaded)'))
print()
rc = ['literal', 'literal-alt', 'literal-i', 'words', 'ing-suffix', 'aws-keys', 'bounded-repeat', 'unicode-upper', 'time']
for h in ['rebar-en-sampled', 'rebar-sherlock']:
    print(table(['count:' + c for c in rc], [h], ['dotnet/interpreted', 'dotnet/compiled', 'dotnet/nonbacktracking', 'rust/regex'], gb, f'{h}, GB/s'))
    print()
