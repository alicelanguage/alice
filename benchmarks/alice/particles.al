# Particle simulation with an @[SoA] store: column updates are vectorized and auto-parallelized.
@[SoA]
record struct Particle(x : Float64, y : Float64, vx : Float64, vy : Float64)

def make(n : Int32) : SoA(Particle)
  ps = SoA(Particle).new
  for i in 0...n
    ps.add(Particle.new((i % 1000) * 0.01, (i % 777) * 0.02, (i % 13) * 0.5 - 3.0, (i % 7) * 0.25))
  end
  ps
end

def kernel(ps : SoA(Particle)) : Int64
  dt = 0.01
  g = 9.81
  for step in 0...10
    ps.each { |p| p.vy = p.vy - g * dt }
    ps.each { |p| p.x = p.x + p.vx * dt }
    ps.each { |p| p.y = p.y + p.vy * dt }
  end
  Convert.ToInt64(ps.sum { |p| p.x + p.y })
end

def measure(ps : SoA(Particle)) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  kernel(ps)
  sw.Elapsed.TotalMilliseconds
end

ps = make(1000000)
check = kernel(make(1000000))
warm = System.Diagnostics.Stopwatch.StartNew
wn = 0
while wn < 50 || warm.Elapsed.TotalMilliseconds < 1000.0
  kernel(ps)
  wn += 1
end
times = Array(Float64).new(15)
for r in 0...15
  times[r] = measure(ps)
end
System.Array.Sort(times)
puts "RESULT particles #{times[7]} #{check}"
