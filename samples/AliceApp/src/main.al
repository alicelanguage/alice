# Alice using a C# project (CSharpLib) and an Alice project (AliceLib).
using CSharpLib
using Geo

xs = [5.0, 1.0, 4.0, 2.0, 3.0]
puts "median from C#: #{Stats.Median(xs)}"

c = Counter.new
10.times { |i| c.Hit(i) }
puts "C# counter: #{c.Count}"

shapes = [Shape::Circle(1.0), Shape::Rect(2.0, 5.0)]
puts "area via AliceLib: #{Geometry.total_area(shapes).round}"
puts "distance: #{Geometry.distance(Point.new(0.0, 0.0), Point.new(6.0, 8.0))}"
