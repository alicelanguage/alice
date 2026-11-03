# fuzz finding strict-rejects-al0220-4258b53f
# category: strict-rejects:AL0220
# main.al:3:8: strict: this range expression has no static type; annotate it, cast it (`x.as(T)`), or declare it `Any` on purpose

def k(n : Int32) : Int32
  case n
  when 3..15
    1
  else
    0
  end
end
puts k(5)
