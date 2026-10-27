// Bend-vs suite in Rust: same algorithms, u32 wrapping, f32 mandelbrot.
// PAR=1 forks with rayon::join above depth CUT; otherwise plain recursion.
use std::sync::atomic::{AtomicBool, Ordering};

static PAR: AtomicBool = AtomicBool::new(false);
const CUT: u32 = 10;

#[inline]
fn fork<A: Send, B: Send>(k: u32, a: impl FnOnce() -> A + Send, b: impl FnOnce() -> B + Send) -> (A, B) {
    if k < CUT && PAR.load(Ordering::Relaxed) { rayon::join(a, b) } else { (a(), b()) }
}

enum Tree { Leaf(u32), Node(Box<Tree>, Box<Tree>) }

fn pow2(n: u32, k: u32) -> u32 {
    if n == 0 { return 1; }
    let (a, b) = fork(k, || pow2(n - 1, k + 1), || pow2(n - 1, k + 1));
    a.wrapping_add(b)
}
fn fib(n: u32, k: u32) -> u32 {
    if n < 2 { return n; }
    let (a, b) = fork(k, || fib(n - 1, k + 1), || fib(n - 2, k + 1));
    a.wrapping_add(b)
}
fn gen(d: u32, x: u32, k: u32) -> Tree {
    if d == 0 { return Tree::Leaf(x); }
    let (a, b) = fork(k, || gen(d - 1, x.wrapping_mul(2), k + 1), || gen(d - 1, x.wrapping_mul(2).wrapping_add(1), k + 1));
    Tree::Node(Box::new(a), Box::new(b))
}
fn total(t: &Tree, k: u32) -> u32 {
    match t {
        Tree::Leaf(v) => *v,
        Tree::Node(l, r) => { let (a, b) = fork(k, || total(l, k + 1), || total(r, k + 1)); a.wrapping_add(b) }
    }
}
fn solve(all: u32, cols: u32, d1: u32, d2: u32, k: u32) -> u32 {
    if cols == all { return 1; }
    place(all & !(cols | d1 | d2), all, cols, d1, d2, k)
}
fn place(avail: u32, all: u32, cols: u32, d1: u32, d2: u32, k: u32) -> u32 {
    if avail == 0 { return 0; }
    let bit = avail & avail.wrapping_neg();
    let (a, b) = fork(k, || solve(all, cols | bit, (d1 | bit) << 1, (d2 | bit) >> 1, k + 1), || place(avail ^ bit, all, cols, d1, d2, k + 1));
    a.wrapping_add(b)
}
fn iter(cr: f32, ci: f32) -> u32 {
    let (mut zr, mut zi, mut i) = (0f32, 0f32, 0u32);
    while i != 256 {
        let (zr2, zi2) = (zr * zr, zi * zi);
        if zr2 + zi2 > 4.0 { break; }
        zi = 2.0 * zr * zi + ci;
        zr = zr2 - zi2 + cr;
        i += 1;
    }
    i
}
fn row(y: u32, w: u32) -> u32 {
    let fw = w as f32;
    let ci = y as f32 * (2.0 / fw) - 1.0;
    let dx = 3.0 / fw;
    let (mut cr, mut acc) = (-2.0f32, 0u32);
    for _ in 0..w { acc = acc.wrapping_add(iter(cr, ci)); cr += dx; }
    acc
}
fn rows(d: u32, y: u32, w: u32, k: u32) -> u32 {
    if d == 0 { return row(y, w); }
    let (a, b) = fork(k, || rows(d - 1, y * 2, w, k + 1), || rows(d - 1, y * 2 + 1, w, k + 1));
    a.wrapping_add(b)
}
fn steps(mut x: u32) -> u32 {
    let mut s = 0;
    while x > 1 && s != 1000 { x = if x & 1 == 1 { x.wrapping_mul(3).wrapping_add(1) } else { x >> 1 }; s += 1; }
    s
}
fn range(d: u32, i: u32, k: u32) -> u32 {
    if d == 0 { return steps(i + 1); }
    let (a, b) = fork(k, || range(d - 1, i * 2, k + 1), || range(d - 1, i * 2 + 1, k + 1));
    a.wrapping_add(b)
}
fn main() {
    let args: Vec<String> = std::env::args().collect();
    PAR.store(std::env::var("PAR").map(|v| v == "1").unwrap_or(false), Ordering::Relaxed);
    let n: u32 = args[2].parse().unwrap();
    let r = match args[1].as_str() {
        "pow2" => pow2(n, 0),
        "fib" => fib(n, 0),
        "tree" => total(&gen(n, 1, 0), 0),
        "nqueens" => solve((1u32 << n) - 1, 0, 0, 0, 0),
        "mandel" => rows(n, 0, 1 << n, 0),
        "collatz" => range(n, 0, 0),
        _ => 0,
    };
    println!("{r}");
}
