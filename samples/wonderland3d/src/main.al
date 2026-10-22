using Raylib_cs
using System.Numerics
using System.Diagnostics
require "./world"
require "./music"

def option(name : String, fallback : String) : String
  i = ARGV.IndexOf(name)
  if i >= 0
    raise ArgumentException.new("Missing value for #{name}") if i + 1 >= ARGV.Length
    return ARGV[i + 1]
  end
  fallback
end

def capture(path : String)
  picture = Raylib.LoadImageFromScreen()
  begin
    ok = Raylib.ExportImage(picture, Path.GetFullPath(path))
    raise IOException.new("Could not save frame: #{path}") if !ok
  ensure
    Raylib.UnloadImage(picture)
  end
end

def lettering(font : Font, text : String, x : Int32, y : Int32, size : Int32, color : Color)
  Raylib.DrawTextEx(font, text, Vector2.new(x.to_f32, y.to_f32), size.to_f32, 1.0f32, color)
end

class ResourceMonitor
  @process : Process
  @last_cpu : Float64
  @last_time : Float64
  @cpu_load : Float64

  def initialize
    @process = Process.GetCurrentProcess()
    @last_cpu = @process.TotalProcessorTime.TotalSeconds
    @last_time = 0.0
    @cpu_load = 0.0
  end

  def update(time : Float64)
    if time - @last_time >= 0.5
      @process.Refresh()
      cpu = @process.TotalProcessorTime.TotalSeconds
      elapsed = time - @last_time
      @cpu_load = Math.Min(100.0, Math.Max(0.0, (cpu - @last_cpu) / elapsed / Environment.ProcessorCount * 100.0))
      @last_cpu = cpu
      @last_time = time
    end
  end

  def cpu : String
    @cpu_load.ToString("0") + "%"
  end

  def ram : String
    (@process.WorkingSet64 / 1_048_576).ToString() + " MB"
  end
end

def captions(t : Float64, w : Int32, h : Int32, sans : Font, serif : Font, stats : ResourceMonitor, elapsed : Float64)
  scale = w.to_f / 1280.0
  left = (55.0 * scale).to_i
  white = Color.new(244, 231, 220)
  muted = Color.new(178, 164, 195)
  stats.update(elapsed)
  Raylib.DrawRectangle(0, 0, w, (30.0 * scale).to_i, Color.new(5, 3, 15))
  Raylib.DrawRectangle(0, h - (30.0 * scale).to_i, w, (30.0 * scale).to_i, Color.new(5, 3, 15))
  lettering(sans, "A L I C E   /   W O N D E R L A N D", left, (49.0 * scale).to_i, (16.0 * scale).to_i, white)
  lettering(sans, "CPU  #{stats.cpu}     RAM  #{stats.ram}", w - (340.0 * scale).to_i, (49.0 * scale).to_i, (15.0 * scale).to_i, muted)
  chapter = Film.chapter(t)
  label = chapter == 0 ? "01   THE GARDEN OF IMPOSSIBLE THINGS" : (chapter == 1 ? "02   SIX O'CLOCK, FOREVER" : "03   DOWN THE RABBIT HOLE")
  lettering(sans, label, left, h - (70.0 * scale).to_i, (17.0 * scale).to_i, white)
  lettering(sans, "MADE WITH ALICE", w - (225.0 * scale).to_i, h - (70.0 * scale).to_i, (15.0 * scale).to_i, muted)
  Raylib.DrawRectangle(left, h - (43.0 * scale).to_i, ((w - left * 2) * t / 30.0).to_i, 2, Color.new(211, 134, 225))
  if t < 3.5 || t > 26.0
    title = t < 3.5 ? "WONDERLAND" : "CURIOUSER & CURIOUSER."
    size = ((t < 3.5 ? 64.0 : 42.0) * scale).to_i
    x = (w - Raylib.MeasureTextEx(serif, title, size.to_f32, 1.0f32).X.to_i) / 2
    lettering(serif, title, x + 2, (h * 0.73).to_i + 2, size, Color.new(20, 7, 35))
    lettering(serif, title, x, (h * 0.73).to_i, size, white)
  end
  # Short fades hide the cuts without interrupting deterministic animation.
  local = t - chapter * 10.0
  fade = Math.Max(0.0, Math.Max(1.0 - local / 0.5, (local - 9.5) / 0.5))
  Raylib.DrawRectangle(0, 0, w, h, Raylib.Fade(Color.new(5, 3, 15), fade.to_f32)) if fade > 0.0
end

width = Int32.Parse(option("--width", "1280"))
height = Int32.Parse(option("--height", "720"))
fps = Int32.Parse(option("--fps", "30"))
raise ArgumentException.new("Use even dimensions >= 320x180 and fps between 1 and 60") if width < 320 || height < 180 || width % 2 != 0 || height % 2 != 0 || fps < 1 || fps > 60
output = option("--render", "")
still = option("--still", "")
at = Float64.Parse(option("--time", "5"), System.Globalization.CultureInfo.InvariantCulture)
raise ArgumentException.new("--time must be in [0, 30)") if at < 0.0 || at >= 30.0
exporting = output != ""
frames = exporting ? Path.Combine(Path.GetTempPath(), "wonderland3d-#{Guid.NewGuid()}") : ""
Directory.CreateDirectory(frames) if exporting
Raylib.SetTraceLogLevel(TraceLogLevel.Warning)
Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint)
Raylib.InitWindow(width, height, "Wonderland3D / Alice")
Raylib.SetTargetFPS(exporting || still != "" ? 0 : fps)
scene = Raylib.LoadRenderTexture(width, height)
velvet = Raylib.LoadShader("shaders/velvet.vs", "shaders/velvet.fs")
bloom = Raylib.LoadShader("shaders/bloom.vs", "shaders/bloom.fs")
sans = Raylib.LoadFontEx("assets/DejaVuSans.ttf", 48, Array(Int32).new(0), 0)
serif = Raylib.LoadFontEx("assets/DejaVuSerif.ttf", 96, Array(Int32).new(0), 0)
Raylib.SetTextureFilter(sans.Texture, TextureFilter.Bilinear)
Raylib.SetTextureFilter(serif.Texture, TextureFilter.Bilinear)
frame = 0
stats = ResourceMonitor.new
begin
  until Raylib.WindowShouldClose()
    t = still != "" ? at : Film.time(frame, fps)
    Raylib.BeginTextureMode(scene)
    Raylib.ClearBackground(Wonderland.ink)
    Raylib.BeginMode3D(Wonderland.camera(t))
    Raylib.BeginShaderMode(velvet)
    Wonderland.draw(t)
    Raylib.EndShaderMode()
    Raylib.EndMode3D()
    Raylib.EndTextureMode()
    Raylib.BeginDrawing()
    Raylib.ClearBackground(Wonderland.ink)
    Raylib.BeginShaderMode(bloom)
    Raylib.DrawTexturePro(scene.Texture, Rectangle.new(0.0f32, 0.0f32, width.to_f32, -height.to_f32), Rectangle.new(0.0f32, 0.0f32, width.to_f32, height.to_f32), Vector2.new(0.0f32, 0.0f32), 0.0f32, Color.new(255,255,255))
    Raylib.EndShaderMode()
    captions(t, width, height, sans, serif, stats, Raylib.GetTime())
    Raylib.EndDrawing()
    if still != ""
      capture(still)
      break
    end
    if exporting
      capture(Path.Combine(frames, "frame#{frame.ToString("D4")}.png"))
      puts "Rendered #{frame + 1}/#{Film.frames(fps)}" if frame % fps == 0
    end
    frame += 1
    break if exporting && frame == Film.frames(fps)
    frame = 0 if !exporting && frame == Film.frames(fps)
  end
ensure
  Raylib.UnloadFont(sans)
  Raylib.UnloadFont(serif)
  Raylib.UnloadShader(bloom)
  Raylib.UnloadShader(velvet)
  Raylib.UnloadRenderTexture(scene)
  Raylib.CloseWindow()
end

if exporting
  raise InvalidOperationException.new("Render cancelled; frames retained at #{frames}") if frame != Film.frames(fps)
  soundtrack = Path.Combine(frames, "wonderland.wav")
  Music.write(soundtrack)
  info = ProcessStartInfo.new("ffmpeg")
  info.UseShellExecute = false
  for arg in ["-hide_banner", "-loglevel", "warning", "-y", "-framerate", fps.ToString(), "-i", Path.Combine(frames, "frame%04d.png"), "-i", soundtrack, "-c:a", "aac", "-b:a", "192k", "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", Path.GetFullPath(output)]
    info.ArgumentList.Add(arg)
  end
  process = Process.new
  process.StartInfo = info
  process.Start()
  process.WaitForExit()
  raise InvalidOperationException.new("FFmpeg failed; frames retained at #{frames}") if process.ExitCode != 0
  Directory.Delete(frames, true)
  puts "Saved 30-second film: #{Path.GetFullPath(output)}"
end
