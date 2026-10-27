import Base
import ./common.bend as C

def iter?(+cr: F32, +ci: F32, +zr: F32, +zi: F32, +i: U32) -> U32:
  iter.a(U32.is_eq(i, 256), cr, ci, zr, zi, i)

def iter.a?(m: Bool, +cr: F32, +ci: F32, +zr: F32, +zi: F32, +i: U32) -> U32:
  match m:
    case True{}:
      i
    case False{}:
      +zr2 = (zr * zr : F32)
      +zi2 = (zi * zi : F32)
      iter.b(F32.is_gt((zr2 + zi2 : F32), 4.0), cr, ci, zr, zi, zr2, zi2, i)

def iter.b?(esc: Bool, +cr: F32, +ci: F32, +zr: F32, +zi: F32, +zr2: F32, +zi2: F32, +i: U32) -> U32:
  match esc:
    case True{}:
      i
    case False{}:
      iter(cr, ci, (zr2 - zi2 + cr : F32), (2.0 * zr * zi + ci : F32), (i + 1 : U32))

def row?(+x: U32, +w: U32, +cr: F32, +dx: F32, +ci: F32, +acc: U32) -> U32:
  row.if(U32.is_eq(x, w), x, w, cr, dx, ci, acc)

def row.if?(e: Bool, +x: U32, +w: U32, +cr: F32, +dx: F32, +ci: F32, +acc: U32) -> U32:
  match e:
    case True{}:
      acc
    case False{}:
      row((x + 1 : U32), w, (cr + dx : F32), dx, ci, (acc + iter(cr, ci, 0.0, 0.0, 0) : U32))

def rows(+d: Nat, +y: U32, +w: U32) -> U32:
  match d:
    case 0n:
      +fw = F32.from_nat(U32.to_nat(w))
      +ci = (F32.from_nat(U32.to_nat(y)) * (2.0 / fw) - 1.0 : F32)
      row(0, w, (0.0 - 2.0 : F32), (3.0 / fw : F32), ci, 0)
    case 1n+p:
      a b = rows(p, (y * 2 : U32), w) rows(p, (y * 2 + 1 : U32), w)
      (a + b : U32)

def main() -> IO(Unit):
  do IO<Unit>:
    args : List<String> <- IO.args()
    +n : U32 = C.arg1(args)
    IO.print(U32.show(rows(U32.to_nat(n), 0, U32.shln(1, U32.to_nat(n)))))
