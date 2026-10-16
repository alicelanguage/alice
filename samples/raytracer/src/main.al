# Ray tracer: alice run --release -- [--size 960x540] [--out wonderland.ppm] [--orbit 48 --frames-dir orbit/]
require "./render"

def arg(name : String, default : String) : String
  i = ARGV.IndexOf(name)
  i < 0 ? default : ARGV[i + 1]
end

size = arg("--size", "960x540").split("x")
w = Int32.Parse(size[0])
h = Int32.Parse(size[1])
scene = Scene.wonderland
orbit = Int32.Parse(arg("--orbit", "0"))

if orbit > 0
  dir = arg("--frames-dir", "orbit")
  Directory.CreateDirectory(dir)
  for f in 0...orbit
    a = 2.0 * Math::PI * f / orbit
    cam = Camera.new(Vec.new(Math.Sin(a) * 7.0, 2.6, -Math.Cos(a) * 7.0), Vec.new(0.0, 0.8, 0.0))
    Render.write_ppm(Path.Combine(dir, "frame#{f.ToString("D3")}.ppm"), Render.image(scene, cam, w, h), w, h)
  end
  puts "wrote #{orbit} frames to #{dir}/"
else
  out = arg("--out", "wonderland.ppm")
  sw = System.Diagnostics.Stopwatch.StartNew
  px = Render.image(scene, Camera.new(Vec.new(0.0, 2.4, -6.5), Vec.new(0.0, 0.8, 0.0)), w, h)
  Render.write_ppm(out, px, w, h)
  puts "rendered #{w}x#{h} (4 samples/pixel) in #{sw.ElapsedMilliseconds} ms → #{out}"
end
