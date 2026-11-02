//! Rust comparators for benchmarks/core (regex, serde_json, simd-json).
//! Protocol (same as the .NET and simdjson drivers):
//!   RESULT <engine> <case> <input> <median_ms> <MB_per_s> <alloc_bytes|-> <checksum>
//! Warm-up: >= WARM_MS ms (default 1000) and >= WARM_N calls (default 50); then TIMED calls (default 15), median reported.
//! `ONCE=1` runs one call without warm-up (cold, for hyperfine).
use std::time::Instant;

mod typed;

fn measure<F: FnMut() -> u64>(engine: &str, case: &str, input: &str, bytes: usize, mut f: F) {
    let once = std::env::var("ONCE").is_ok();
    let warm_ms: u128 = std::env::var("WARM_MS").ok().and_then(|s| s.parse().ok()).unwrap_or(1000);
    let warm_n: usize = std::env::var("WARM_N").ok().and_then(|s| s.parse().ok()).unwrap_or(50);
    let timed: usize = std::env::var("TIMED").ok().and_then(|s| s.parse().ok()).unwrap_or(15);
    let reference = f();
    if !once {
        let t0 = Instant::now();
        let mut n = 0;
        while n < warm_n || t0.elapsed().as_millis() < warm_ms {
            assert_eq!(f(), reference);
            n += 1;
        }
    }
    let mut ts = Vec::new();
    for _ in 0..(if once { 1 } else { timed }) {
        let t = Instant::now();
        let c = f();
        ts.push(t.elapsed().as_secs_f64() * 1e3);
        assert_eq!(c, reference, "checksum changed");
    }
    ts.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let med = ts[ts.len() / 2];
    let mbs = bytes as f64 / 1e6 / (med / 1e3);
    println!("RESULT {engine} {case} {input} {med:.4} {mbs:.1} - {reference}");
}

fn name_of(path: &str) -> String {
    std::path::Path::new(path).file_stem().unwrap().to_string_lossy().into_owned()
}

fn count_value(v: &serde_json::Value) -> u64 {
    match v {
        serde_json::Value::Array(a) => a.len() as u64,
        serde_json::Value::Object(o) => o.len() as u64,
        _ => 1,
    }
}

fn json_dom(path: &str) {
    let data = std::fs::read(path).unwrap();
    let input = name_of(path);
    measure("serde_json/value", "json-dom", &input, data.len(), || {
        let v: serde_json::Value = serde_json::from_slice(&data).unwrap();
        count_value(&v)
    });
    let mut scratch = data.clone();
    measure("simd-json/borrowed", "json-dom", &input, data.len(), || {
        // simd-json parses in place; the copy is part of the cost (as in serde-rs/json-benchmark).
        scratch.copy_from_slice(&data);
        let v = simd_json::to_borrowed_value(&mut scratch).unwrap();
        match &v {
            simd_json::BorrowedValue::Array(a) => a.len() as u64,
            simd_json::BorrowedValue::Object(o) => o.len() as u64,
            _ => 1,
        }
    });
}

fn json_typed(kind: &str, path: &str) {
    let data = std::fs::read(path).unwrap();
    let input = name_of(path);
    macro_rules! both {
        ($t:ty, $sum:expr) => {{
            measure("serde_json/typed", "json-typed", &input, data.len(), || {
                let v: $t = serde_json::from_slice(&data).unwrap();
                $sum(&v)
            });
            let mut scratch = data.clone();
            measure("simd-json/typed", "json-typed", &input, data.len(), || {
                scratch.copy_from_slice(&data);
                let v: $t = simd_json::serde::from_slice(&mut scratch).unwrap();
                $sum(&v)
            });
        }};
    }
    match kind {
        "twitter" => both!(typed::Twitter, |v: &typed::Twitter| v.statuses.len() as u64
            + v.statuses.iter().map(|s| s.user.followers_count).sum::<u64>()),
        "canada" => both!(typed::Canada, |v: &typed::Canada| v
            .features
            .iter()
            .map(|f| f.geometry.coordinates.iter().map(|r| r.len() as u64).sum::<u64>())
            .sum::<u64>()),
        "citm_catalog" => both!(typed::Citm, |v: &typed::Citm| v.performances.len() as u64 + v.events.len() as u64),
        _ => panic!("unknown typed kind {kind}"),
    }
}

const VARIANTS: [&str; 9] = [
    "agggtaaa|tttaccct",
    "[cgt]gggtaaa|tttaccc[acg]",
    "a[act]ggtaaa|tttacc[agt]t",
    "ag[act]gtaaa|tttac[agt]ct",
    "agg[act]taaa|ttta[agt]cct",
    "aggg[acg]aaa|ttt[cgt]ccct",
    "agggt[cgt]aa|tt[acg]accct",
    "agggta[cgt]a|t[acg]taccct",
    "agggtaa[cgt]|[acg]ttaccct",
];
const SUBST: [(&str, &str); 5] = [
    ("tHa[Nt]", "<4>"),
    ("aND|caN|Ha[DS]|WaS", "<3>"),
    ("a[NSt]|BY", "<2>"),
    ("<[^>]*>", "|"),
    ("\\|[^|][^|]*\\|", "-"),
];

/// regex-redux, single-threaded (engine comparison, not the Benchmarks Game multi-threaded entries).
fn regex_redux(path: &str) {
    use regex::bytes::Regex;
    let data = std::fs::read(path).unwrap();
    let strip = Regex::new(">[^\n]*\n|\n").unwrap();
    let variants: Vec<Regex> = VARIANTS.iter().map(|p| Regex::new(p).unwrap()).collect();
    let subst: Vec<(Regex, &[u8])> = SUBST.iter().map(|(p, r)| (Regex::new(p).unwrap(), r.as_bytes())).collect();
    let mut printed = false;
    measure("rust/regex", "regex-redux", "fasta-5M", data.len(), || {
        let seq = strip.replace_all(&data, &b""[..]).into_owned();
        let mut out = String::new();
        let mut sum = 0u64;
        for (p, r) in VARIANTS.iter().zip(&variants) {
            let c = r.find_iter(&seq).count();
            sum += c as u64;
            out += &format!("{} {}\n", p, c);
        }
        let mut s = seq.clone();
        for (r, rep) in &subst {
            s = r.replace_all(&s, *rep).into_owned();
        }
        out += &format!("\n{}\n{}\n{}\n", data.len(), seq.len(), s.len());
        if !printed {
            eprint!("{out}");
            printed = true;
        }
        sum * 1_000_000_000 + s.len() as u64
    });
}

pub const REBAR: [(&str, &str); 9] = [
    ("literal", "Sherlock Holmes"),
    ("literal-alt", "Sherlock Holmes|John Watson|Irene Adler|Inspector Lestrade|Professor Moriarty"),
    ("literal-i", "(?i)Sherlock Holmes"),
    ("words", "\\b\\w+\\b"),
    ("ing-suffix", "[a-zA-Z]+ing"),
    ("aws-keys", "(?:AKIA|ASIA)[A-Z0-9]{16}"),
    ("bounded-repeat", "[A-Za-z]{8,13}"),
    ("unicode-upper", "\\p{Lu}\\p{Ll}+"),
    ("time", "[0-9]{1,2}:[0-9]{2}"),
];

fn rebar(path: &str) {
    let data = std::fs::read(path).unwrap();
    let input = name_of(path);
    for (case, pat) in REBAR {
        let r = regex::bytes::Regex::new(pat).unwrap();
        measure("rust/regex", &format!("count:{case}"), &input, data.len(), || r.find_iter(&data).count() as u64);
    }
}

fn main() {
    let a: Vec<String> = std::env::args().collect();
    match a.get(1).map(|s| s.as_str()) {
        Some("json-dom") => json_dom(&a[2]),
        Some("json-typed") => json_typed(&a[2], &a[3]),
        Some("regex-redux") => regex_redux(&a[2]),
        Some("rebar") => rebar(&a[2]),
        _ => {
            eprintln!("usage: core-baselines json-dom <file> | json-typed <twitter|canada|citm_catalog> <file> | regex-redux <file> | rebar <file>");
            std::process::exit(2)
        }
    }
}
