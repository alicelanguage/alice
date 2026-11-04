enum S0
  Circle(r : Int32)
  Rect(w : Int32, h : Int64)
  Empty
end
record struct R2(a : Int32, b : Int64, c : Float64)
def area0(s : S0) : Int64
  case s
  when S0::Circle(r) then r.to_i64 &* r.to_i64 &* 3_i64
  when S0::Rect(w, h) then w.to_i64 &* h
  when S0::Empty then 4_i64
  end
end
def k0(n : Int32) : Int64
  l = List(S0).new
  for x in 0...n
    if x.even?
      l << S0::Circle((x * (-2147483648)))
      l << S0::Rect((-x), ((x == x) ? 4651_i64 : 3247_i64))
    end
  end
  t = 0_i64
  t
end
def k1(n : Int32)
end
def k2(n : Int32) : Int64
  p = R2.new(n, 234_i64, (-3.25).round)
  q = R2.new(p.a &+ n, p.b &* 3_i64, p.c * 0.5)
  p.a.to_i64 &+ q.b &+ q.a.to_i64
end
begin
  puts k0(4)
end