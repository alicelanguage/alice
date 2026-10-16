// Plain C# using types written in Alice: records, sum types, modules. No generated code is visible here.
using Geo;

var d = Geometry.distance(new Point(0, 0), new Point(3, 4));
Shape[] shapes = [new Shape.Circle(1), new Shape.Rect(2, 3)];
Console.WriteLine($"distance={d} area={Geometry.total_area(shapes):0.00}");
Console.WriteLine(shapes[1] is Shape.Rect { w: 2 } ? "pattern-matched an Alice sum type from C#" : "?");
