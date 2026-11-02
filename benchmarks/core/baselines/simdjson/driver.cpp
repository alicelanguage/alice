// simdjson comparator for benchmarks/core: DOM parse (reused parser) and On-Demand full walk.
// Same protocol as baselines/rust: RESULT <engine> <case> <input> <median_ms> <MB_per_s> - <checksum>
// Build: see build.sh (clang++ -O3 -march=native over the single-header amalgamation).
#include "simdjson.h"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

using namespace simdjson;
using clk = std::chrono::steady_clock;

template <class F> static void measure(const char *engine, const char *kase, const std::string &input, size_t bytes, F f) {
  const char *w = getenv("WARM_MS"), *t = getenv("TIMED"), *wn = getenv("WARM_N");
  double warm_ms = w ? atof(w) : 1000; int timed = t ? atoi(t) : 15, warm_n = wn ? atoi(wn) : 50; bool once = getenv("ONCE") != nullptr;
  uint64_t ref = f();
  if (!once) {
    auto t0 = clk::now(); int n = 0;
    while (n < warm_n || std::chrono::duration<double, std::milli>(clk::now() - t0).count() < warm_ms) { if (f() != ref) abort(); n++; }
  }
  std::vector<double> ts;
  for (int i = 0; i < (once ? 1 : timed); i++) {
    auto a = clk::now(); uint64_t c = f(); auto b = clk::now();
    if (c != ref) abort();
    ts.push_back(std::chrono::duration<double, std::milli>(b - a).count());
  }
  std::sort(ts.begin(), ts.end());
  double med = ts[ts.size() / 2];
  printf("RESULT %s %s %s %.4f %.1f - %llu\n", engine, kase, input.c_str(), med, bytes / 1e6 / (med / 1e3), (unsigned long long)ref);
}

// Counts every value: forces On-Demand to visit the whole document (comparable to a DOM build).
static uint64_t walk(ondemand::value v) {
  switch (v.type()) {
  case ondemand::json_type::array: { uint64_t n = 1; for (auto e : v.get_array()) n += walk(e.value()); return n; }
  case ondemand::json_type::object: { uint64_t n = 1; for (auto f : v.get_object()) { (void)f.unescaped_key(); n += walk(f.value()); } return n; }
  case ondemand::json_type::number: (void)v.get_double(); return 1;
  case ondemand::json_type::string: (void)v.get_string(); return 1;
  case ondemand::json_type::boolean: (void)v.get_bool(); return 1;
  default: (void)v.is_null(); return 1;
  }
}

static uint64_t count(dom::element e) {
  if (e.is_array()) { uint64_t n = 1; for (auto c : dom::array(e)) n += count(c); return n; }
  if (e.is_object()) { uint64_t n = 1; for (auto f : dom::object(e)) n += count(f.value); return n; }
  return 1;
}

int main(int argc, char **argv) {
  if (argc < 2) { fprintf(stderr, "usage: simdjson-driver <file.json>...\n"); return 2; }
  fprintf(stderr, "simdjson %s, implementation %s\n", SIMDJSON_VERSION, get_active_implementation()->name().c_str());
  for (int i = 1; i < argc; i++) {
    padded_string json = padded_string::load(argv[i]);
    std::string name = argv[i]; name = name.substr(name.find_last_of('/') + 1); name = name.substr(0, name.rfind('.'));
    dom::parser dp;
    uint64_t nodes = count(dp.parse(json));
    measure("simdjson/dom", "json-dom", name, json.size(), [&]() -> uint64_t { dom::element e = dp.parse(json); return e.is_array() ? dom::array(e).size() : e.is_object() ? dom::object(e).size() : 1; });
    ondemand::parser op;
    measure("simdjson/ondemand-walk", "json-walk", name, json.size(), [&]() -> uint64_t { auto doc = op.iterate(json); return walk(doc.get_value()); });
    fprintf(stderr, "%s: %llu nodes\n", name.c_str(), (unsigned long long)nodes);
  }
}
