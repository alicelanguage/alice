import Base

def unwrap(m: Maybe<&2, U32>) -> U32:
  match m:
    case Some{v}:
      v
    case None{}:
      0

def arg1(xs: List<String>) -> U32:
  match xs:
    case Con{h, t}:
      match t:
        case Con{s, r}:
          unwrap(U32.read(s))
        case Nil{}:
          0
    case Nil{}:
      0
