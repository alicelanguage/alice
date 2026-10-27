import Base
import ./common.bend as C

def fib?(+n: Nat) -> U32:
  match n:
    case 0n:
      0
    case 1n+p:
      match p:
        case 0n:
          1
        case 1n+q:
          a b = fib(p) fib(q)
          (a + b : U32)

def main() -> IO(Unit):
  do IO<Unit>:
    args : List<String> <- IO.args()
    n : U32 = C.arg1(args)
    IO.print(U32.show(fib(U32.to_nat(n))))
