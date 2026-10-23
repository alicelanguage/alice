# .NET API shapes from Alice: iterators, events, delegates, default interface methods, method references, records
# with attributes for JSON, lock.
using System.Text.Json
using System.Text.Json.Serialization

record Range(start : Int32, @[JsonPropertyName("end")] stop : Int32)   # "end" is an Alice keyword

def evens(limit : Int32) : IEnumerable(Int32)                            # lazy: yield return
  i = 0
  while true
    return if i > limit
    yield i
    i += 2
  end
end

delegate Transform(x : Int32) : Int32

interface IShape
  def area : Float64
  def describe : String                                                  # default implementation
    "shape of area #{area}"
  end
end

class Square < IShape
  getter side : Float64
  event resized : EventHandler
  def initialize(@side : Float64)
  end
  def area : Float64
    @side * @side
  end
  def grow
    @side += 1.0
    resized&.Invoke(self, EventArgs.Empty)
  end
end

def triple(x : Int32) : Int32
  x * 3
end

def apply(t : Transform, x : Int32) : Int32
  t.Invoke(x)
end

puts evens(10).ToList().join(",")
puts JsonSerializer.Serialize(Range.new(2, 9))
puts apply(->triple, 5)
puts [1, 2, 3].map(->triple).join(",")
sq = Square.new(2.0)
grown = 0
sq.resized += ->(s : Object?, e : EventArgs) { grown += 1 }
sq.grow
puts "#{sq.as(IShape).describe}, grown #{grown}"
gate = System.Threading.Lock.new
lock gate
  puts "locked section"
end
