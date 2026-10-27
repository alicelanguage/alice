import Base
import ./common.bend as C

def steps?(+x: U32, +s: U32) -> U32:
  steps.a(U32.is_le(x, 1) || U32.is_eq(s, 1000), x, s)

def steps.a?(stop: Bool, +x: U32, +s: U32) -> U32:
  match stop:
    case True{}:
      s
    case False{}:
      steps.b(U32.is_even(x), x, s)

def steps.b?(e: Bool, +x: U32, +s: U32) -> U32:
  match e:
    case True{}:
      steps(U32.shr(x), (s + 1 : U32))
    case False{}:
      steps((x * 3 + 1 : U32), (s + 1 : U32))

def range(+d: Nat, +i: U32) -> U32:
  match d:
    case 0n:
      steps((i + 1 : U32), 0)
    case 1n+p:
      a b = range(p, (i * 2 : U32)) range(p, (i * 2 + 1 : U32))
      (a + b : U32)

def main() -> IO(Unit):
  do IO<Unit>:
    args : List<String> <- IO.args()
    n : U32 = C.arg1(args)
    IO.print(U32.show(range(U32.to_nat(n), 0)))
