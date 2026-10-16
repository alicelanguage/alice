# nbody with the hot loops in @[Native] functions (compiled by clang -O3 -march=native; managed fallback otherwise).
@[Native]
def advance(x : Array(Float64), y : Array(Float64), z : Array(Float64), vx : Array(Float64), vy : Array(Float64), vz : Array(Float64), m : Array(Float64), steps : Int32)
  n = x.size
  dt = 0.01
  for s in 0...steps
    for i in 0...n
      for j in (i + 1)...n
        dx = x[i] - x[j]
        dy = y[i] - y[j]
        dz = z[i] - z[j]
        d2 = dx * dx + dy * dy + dz * dz
        mag = dt / (d2 * Math.Sqrt(d2))
        vx[i] -= dx * m[j] * mag
        vy[i] -= dy * m[j] * mag
        vz[i] -= dz * m[j] * mag
        vx[j] += dx * m[i] * mag
        vy[j] += dy * m[i] * mag
        vz[j] += dz * m[i] * mag
      end
    end
    for i in 0...n
      x[i] += dt * vx[i]
      y[i] += dt * vy[i]
      z[i] += dt * vz[i]
    end
  end
end

@[Native]
def energy(x : Array(Float64), y : Array(Float64), z : Array(Float64), vx : Array(Float64), vy : Array(Float64), vz : Array(Float64), m : Array(Float64)) : Float64
  n = x.size
  e = 0.0
  for i in 0...n
    e += 0.5 * m[i] * (vx[i] * vx[i] + vy[i] * vy[i] + vz[i] * vz[i])
    for j in (i + 1)...n
      dx = x[i] - x[j]
      dy = y[i] - y[j]
      dz = z[i] - z[j]
      e -= m[i] * m[j] / Math.Sqrt(dx * dx + dy * dy + dz * dz)
    end
  end
  e
end

def kernel(steps : Int32) : Int64
  pi = 3.141592653589793
  sm = 4.0 * pi * pi
  dp = 365.24
  x = [0.0, 4.84143144246472090e+00, 8.34336671824457987e+00, 1.28943695621391310e+01, 1.53796971148509165e+01]
  y = [0.0, -1.16032004402742839e+00, 4.12479856412430479e+00, -1.51111514016986312e+01, -2.59193146099879641e+01]
  z = [0.0, -1.03622044471123109e-01, -4.03523417114321381e-01, -2.23307578892655734e-01, 1.79258772950371181e-01]
  vx = [0.0, 1.66007664274403694e-03 * dp, -2.76742510726862411e-03 * dp, 2.96460137564761618e-03 * dp, 2.68067772490389322e-03 * dp]
  vy = [0.0, 7.69901118419740425e-03 * dp, 4.99852801234917238e-03 * dp, 2.37847173959480950e-03 * dp, 1.62824170038242295e-03 * dp]
  vz = [0.0, -6.90460016972063023e-05 * dp, 2.30417297573763929e-05 * dp, -2.96589568540237556e-05 * dp, -9.51592254519715870e-05 * dp]
  m = [sm, 9.54791938424326609e-04 * sm, 2.85885980666130812e-04 * sm, 4.36624404335156298e-05 * sm, 5.15138902046611451e-05 * sm]
  px = 0.0
  py = 0.0
  pz = 0.0
  for i in 0...5
    px += vx[i] * m[i]
    py += vy[i] * m[i]
    pz += vz[i] * m[i]
  end
  vx[0] = -px / sm
  vy[0] = -py / sm
  vz[0] = -pz / sm
  advance(x, y, z, vx, vy, vz, m, steps)
  Convert.ToInt64(energy(x, y, z, vx, vy, vz, m) * 1000000000.0)
end

def measure(steps : Int32, check : Int64) : Float64
  sw = System.Diagnostics.Stopwatch.StartNew
  got = kernel(steps)
  raise "checksum mismatch" if got != check
  sw.Elapsed.TotalMilliseconds
end

steps = 1000000
check = kernel(steps)
warm = System.Diagnostics.Stopwatch.StartNew
wn = 0
while wn < 5 || warm.Elapsed.TotalMilliseconds < 1000.0
  kernel(steps / 50)
  wn += 1
end
times = Array(Float64).new(15)
for r in 0...15
  times[r] = measure(steps, check)
end
System.Array.Sort(times)
puts "RESULT nbody_native #{times[7]} #{check} native=#{__alice_native_loaded()}"
