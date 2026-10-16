# 3D vector as a value type: operators compile to static C# operators (no allocation).
record struct Vec(x : Float64, y : Float64, z : Float64)
  def +(o : Vec) : Vec
    Vec.new(x + o.x, y + o.y, z + o.z)
  end

  def -(o : Vec) : Vec
    Vec.new(x - o.x, y - o.y, z - o.z)
  end

  def *(k : Float64) : Vec
    Vec.new(x * k, y * k, z * k)
  end

  def mul(o : Vec) : Vec
    Vec.new(x * o.x, y * o.y, z * o.z)
  end

  def dot(o : Vec) : Float64
    x * o.x + y * o.y + z * o.z
  end

  def length : Float64
    Math.Sqrt(dot(self))
  end

  def unit : Vec
    self * (1.0 / length)
  end

  def reflect(n : Vec) : Vec
    self - n * (2.0 * dot(n))
  end
end
