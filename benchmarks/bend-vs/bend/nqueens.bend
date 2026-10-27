import Base
import ./common.bend as C

def solve?(+all: U32, +cols: U32, +d1: U32, +d2: U32) -> U32:
  solve.if(U32.is_eq(cols, all), all, cols, d1, d2)

def solve.if?(c: Bool, +all: U32, +cols: U32, +d1: U32, +d2: U32) -> U32:
  match c:
    case True{}:
      1
    case False{}:
      place((all .&. U32.not((cols .|. d1 .|. d2 : U32)) : U32), all, cols, d1, d2)

def place?(+avail: U32, +all: U32, +cols: U32, +d1: U32, +d2: U32) -> U32:
  place.if(U32.is_zero(avail), avail, all, cols, d1, d2)

def place.if?(z: Bool, +avail: U32, +all: U32, +cols: U32, +d1: U32, +d2: U32) -> U32:
  match z:
    case True{}:
      0
    case False{}:
      +bit = (avail .&. (0 - avail) : U32)
      a b = solve(all, (cols .|. bit : U32), U32.shl((d1 .|. bit : U32)), U32.shr((d2 .|. bit : U32))) place((avail .^. bit : U32), all, cols, d1, d2)
      (a + b : U32)

def main() -> IO(Unit):
  do IO<Unit>:
    args : List<String> <- IO.args()
    n : U32 = C.arg1(args)
    IO.print(U32.show(solve((U32.shln(1, U32.to_nat(n)) - 1 : U32), 0, 0, 0)))
