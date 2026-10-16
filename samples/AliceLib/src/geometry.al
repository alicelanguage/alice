# An Alice library consumed by plain C# (see ../CSharpApp).
namespace Geo
  record Point(x : Float64, y : Float64)

  enum Shape
    Circle(r : Float64)
    Rect(w : Float64, h : Float64)
  end

  module Geometry
    def self.distance(a : Point, b : Point) : Float64
      dx = a.x - b.x
      dy = a.y - b.y
      Math.Sqrt(dx * dx + dy * dy)
    end

    def self.area(s : Shape) : Float64
      case s
      when Shape::Circle(r) then Math::PI * r * r
      when Shape::Rect(w, h) then w * h
      end
    end

    def self.total_area(shapes : Array(Shape)) : Float64
      shapes.sum { |s| area(s) }
    end
  end
end
