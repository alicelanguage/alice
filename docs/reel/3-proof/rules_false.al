# FALSE rule: subtraction is NOT commutative
rule sub_commutes(a in -100..100, b in -100..100)
  a - b == b - a
end
