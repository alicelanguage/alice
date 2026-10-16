# Brick-break sparks stored column-wise (@[SoA]); updates are vectorized column loops.

@[SoA]
record struct Spark(x : Float64, y : Float64, vx : Float64, vy : Float64, life : Float64, hue : Float64)

class Particles
  getter store : SoA(Spark) = SoA(Spark).new
  @rng : Random = Random.new(7)

  def size : Int32
    store.size
  end

  def burst(x : Float64, y : Float64, hue : Float64, n : Int32)
    n.times do |i|
      a = @rng.NextDouble * Math::PI * 2.0
      s = 60.0 + @rng.NextDouble * 180.0
      store.add(Spark.new(x, y, Math.Cos(a) * s, Math.Sin(a) * s, 0.4 + @rng.NextDouble * 0.5, hue))
    end
  end

  def update(dt : Float64)
    g = 500.0 * dt
    store.each { |p| p.vy = p.vy + g }
    store.each { |p| p.x = p.x + p.vx * dt }
    store.each { |p| p.y = p.y + p.vy * dt }
    store.each { |p| p.life = p.life - dt }
    i = store.size - 1
    while i >= 0
      store.remove_at(i) if store[i].life <= 0.0
      i -= 1
    end
  end

  def clear
    store.clear
  end
end
