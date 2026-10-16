class Body
  property x : Float64 = 0.0
  property y : Float64 = 0.0
  property z : Float64 = 0.0
  property vx : Float64 = 0.0
  property vy : Float64 = 0.0
  property vz : Float64 = 0.0
  property mass : Float64 = 0.0
  def initialize(@x : Float64, @y : Float64, @z : Float64, @vx : Float64, @vy : Float64, @vz : Float64, @mass : Float64)
  end
end

def make_bodies : Array(Body)
  pi = 3.141592653589793
  sm = 4.0 * pi * pi
  dp = 365.24
  bodies = [
  Body.new(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, sm),
  Body.new(4.84143144246472090e+00, -1.16032004402742839e+00, -1.03622044471123109e-01, 1.66007664274403694e-03 * dp, 7.69901118419740425e-03 * dp, -6.90460016972063023e-05 * dp, 9.54791938424326609e-04 * sm),
  Body.new(8.34336671824457987e+00, 4.12479856412430479e+00, -4.03523417114321381e-01, -2.76742510726862411e-03 * dp, 4.99852801234917238e-03 * dp, 2.30417297573763929e-05 * dp, 2.85885980666130812e-04 * sm),
  Body.new(1.28943695621391310e+01, -1.51111514016986312e+01, -2.23307578892655734e-01, 2.96460137564761618e-03 * dp, 2.37847173959480950e-03 * dp, -2.96589568540237556e-05 * dp, 4.36624404335156298e-05 * sm),
  Body.new(1.53796971148509165e+01, -2.59193146099879641e+01, 1.79258772950371181e-01, 2.68067772490389322e-03 * dp, 1.62824170038242295e-03 * dp, -9.51592254519715870e-05 * dp, 5.15138902046611451e-05 * sm)
  ]
  px = 0.0
  py = 0.0
  pz = 0.0
  for b in bodies
    px += b.vx * b.mass
    py += b.vy * b.mass
    pz += b.vz * b.mass
  end
  s = bodies[0]
  s.vx = -px / sm
  s.vy = -py / sm
  s.vz = -pz / sm
  bodies
end

def advance(bodies : Array(Body), dt : Float64)
  n = bodies.size
  for i in 0...n
    a = bodies[i]
    for j in (i + 1)...n
      b = bodies[j]
      dx = a.x - b.x
      dy = a.y - b.y
      dz = a.z - b.z
      d2 = dx * dx + dy * dy + dz * dz
      mag = dt / (d2 * Math.Sqrt(d2))
      a.vx -= dx * b.mass * mag
      a.vy -= dy * b.mass * mag
      a.vz -= dz * b.mass * mag
      b.vx += dx * a.mass * mag
      b.vy += dy * a.mass * mag
      b.vz += dz * a.mass * mag
    end
  end
  for b in bodies
    b.x += dt * b.vx
    b.y += dt * b.vy
    b.z += dt * b.vz
  end
end

def energy(bodies : Array(Body)) : Float64
  e = 0.0
  n = bodies.size
  for i in 0...n
    a = bodies[i]
    e += 0.5 * a.mass * (a.vx * a.vx + a.vy * a.vy + a.vz * a.vz)
    for j in (i + 1)...n
      b = bodies[j]
      dx = a.x - b.x
      dy = a.y - b.y
      dz = a.z - b.z
      e -= a.mass * b.mass / Math.Sqrt(dx * dx + dy * dy + dz * dz)
    end
  end
  e
end

def kernel(steps : Int32) : Int64
  bodies = make_bodies
  for s in 0...steps
    advance(bodies, 0.01)
  end
  Convert.ToInt64(energy(bodies) * 1000000000.0)
end

def measure(steps : Int32, check : Int64) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  got = kernel(steps)
  raise "checksum mismatch" if got != check
  sw.Elapsed.TotalMilliseconds
end

steps = 1000000
check = kernel(steps)
# warm-up: >= 50 calls and >= 1 s so tier-1 + PGO code replaces tier-0/OSR
warm = System.Diagnostics.Stopwatch.StartNew
wn = 0
while wn < 50 || warm.Elapsed.TotalMilliseconds < 1000.0
  kernel(steps / 50)
  wn += 1
end
times = Array(Float64).new(15)
for r in 0...15
  times[r] = measure(steps, check)
end
System.Array.Sort(times)
puts "RESULT nbody #{times[7]} #{check}"
