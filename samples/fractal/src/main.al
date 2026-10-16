# Mandelbrot zoom: alice run --release -- [--frames 60] [--size 480x320] [--out zoom]
require "./fractal"

def arg(name : String, default : String) : String
  i = ARGV.IndexOf(name)
  i < 0 ? default : ARGV[i + 1]
end

frames = Int32.Parse(arg("--frames", "60"))
size = arg("--size", "480x320").split("x")
w = Int32.Parse(size[0])
h = Int32.Parse(size[1])
dir = arg("--out", "zoom")
Directory.CreateDirectory(dir)

# Seahorse valley: zoom 2.6x per second of a 30 fps video
cx = -0.743643887037151
cy = 0.131825904205330
sw = System.Diagnostics.Stopwatch.StartNew
for f in 0...frames
  scale = 3.2 / w * Math.Pow(0.94, f)
  iters = 200 + f * 12
  Fractal.write_ppm(Path.Combine(dir, "frame#{f.ToString("D3")}.ppm"), Fractal.render(cx, cy, scale, w, h, iters), w, h)
end
puts "#{frames} frames #{w}x#{h} in #{sw.ElapsedMilliseconds} ms → #{dir}/"
