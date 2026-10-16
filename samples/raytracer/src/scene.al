require "./vec"

record struct Ray(origin : Vec, dir : Vec)

class Sphere
  getter center : Vec
  getter radius : Float64
  getter color : Vec
  getter mirror : Float64

  def initialize(@center : Vec, @radius : Float64, @color : Vec, @mirror : Float64)
  end

  # Distance along the ray to the nearest hit in front of it, or -1.0.
  def hit(r : Ray) : Float64
    oc = r.origin - @center
    b = oc.dot(r.dir)
    c = oc.dot(oc) - @radius * @radius
    disc = b * b - c
    return -1.0 if disc < 0.0
    s = Math.Sqrt(disc)
    t = -b - s
    t = -b + s if t < 0.001
    t < 0.001 ? -1.0 : t
  end
end

IVORY = Vec.new(0.96, 0.94, 0.88)
INK = Vec.new(0.08, 0.07, 0.06)
CHERRY = Vec.new(0.76, 0.09, 0.24)

class Scene
  getter spheres : List(Sphere) = List(Sphere).new
  getter light : Vec = Vec.new(-0.5, 1.0, -0.6).unit

  def self.wonderland : Scene
    s = Scene.new
    s.spheres.Add(Sphere.new(Vec.new(0.0, 1.0, 0.0), 1.0, IVORY, 0.05))
    s.spheres.Add(Sphere.new(Vec.new(-2.1, 0.7, 0.9), 0.7, CHERRY, 0.15))
    s.spheres.Add(Sphere.new(Vec.new(2.0, 0.8, 0.6), 0.8, Vec.new(0.9, 0.9, 0.95), 0.85))
    s.spheres.Add(Sphere.new(Vec.new(0.9, 0.35, -1.4), 0.35, INK, 0.3))
    s
  end

  # Floor y = 0: a playing-card checkerboard of ivory and cherry.
  def floor_color(p : Vec) : Vec
    ((p.x.floor + p.z.floor).to_i % 2 == 0) ? IVORY * 0.8 : CHERRY * 0.9
  end

  def shadowed?(p : Vec) : Bool
    r = Ray.new(p, @light)
    @spheres.any? { |s| s.hit(r) > 0.0 }
  end

  def trace(r : Ray, depth : Int32) : Vec
    best = 1.0e30
    hit_sphere : Sphere? = nil
    @spheres.each do |s|
      t = s.hit(r)
      if t > 0.0 && t < best
        best = t
        hit_sphere = s
      end
    end
    floor_t = r.dir.y < -0.0001 ? -r.origin.y / r.dir.y : -1.0
    sph = hit_sphere
    if sph.nil? && floor_t < 0.0
      k = 0.5 * (r.dir.y + 1.0)
      return INK * (1.0 - k) + Vec.new(0.18, 0.12, 0.16) * k
    end
    if !sph.nil? && (floor_t < 0.0 || best < floor_t)
      p = r.origin + r.dir * best
      n = (p - sph.center).unit
      base = sph.color
      mirror = sph.mirror
    else
      p = r.origin + r.dir * floor_t
      n = Vec.new(0.0, 1.0, 0.0)
      base = floor_color(p)
      mirror = 0.12
    end
    diffuse = Math.Max(0.0, n.dot(@light))
    diffuse *= 0.25 if shadowed?(p + n * 0.001)
    spec = Math.Pow(Math.Max(0.0, r.dir.reflect(n).dot(@light)), 40.0)
    color = base * (0.12 + 0.88 * diffuse) + Vec.new(1.0, 1.0, 1.0) * (spec * 0.6)
    if mirror > 0.0 && depth < 3
      refl = trace(Ray.new(p + n * 0.001, r.dir.reflect(n).unit), depth + 1)
      color = color * (1.0 - mirror) + refl * mirror
    end
    color
  end
end
