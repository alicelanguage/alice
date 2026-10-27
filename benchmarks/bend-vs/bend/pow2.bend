import Base
import ./common.bend as C

def pow2(+n: Nat) -> U32:
  match n:
    case 0n:
      1
    case 1n+p:
      a b = pow2(p) pow2(p)
      (a + b : U32)

def main() -> IO(Unit):
  do IO<Unit>:
    args : List<String> <- IO.args()
    n : U32 = C.arg1(args)
    IO.print(U32.show(pow2(U32.to_nat(n))))
