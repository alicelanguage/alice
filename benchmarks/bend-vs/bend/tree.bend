import Base
import ./common.bend as C

type Tree is Type:
  Leaf{v: U32}
  Node{l: Tree, r: Tree}

def gen(+d: Nat, +x: U32) -> Tree:
  match d:
    case 0n:
      Leaf{x}
    case 1n+p:
      a b = gen(p, (x * 2 : U32)) gen(p, (x * 2 + 1 : U32))
      Node{a, b}

def total(t: Tree) -> U32:
  match t:
    case Leaf{v}:
      v
    case Node{l, r}:
      a b = total(l) total(r)
      (a + b : U32)

def main() -> IO(Unit):
  do IO<Unit>:
    args : List<String> <- IO.args()
    n : U32 = C.arg1(args)
    IO.print(U32.show(total(gen(U32.to_nat(n), 1))))
