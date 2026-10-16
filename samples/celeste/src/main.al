# Only the platform adapter uses raylib; the PICO-8 game, rendering and persistence are Alice.
using Raylib_cs
require "./classic"
require "./persistence"

class Desktop
  @e : Pico = Pico.new(1)
  @g : Classic
  @paused : Bool = false
  @recording : Bool = false
  @recorded : List(InputFrame) = List(InputFrame).new
  @playback : Array(InputFrame) = Array(InputFrame).new(0)
  @play_index : Int32 = 0
  @replaying : Bool = false
  @message : String = "Z JUMP   X DASH   ARROWS MOVE"
  @message_time : Int32 = 0
  @colors : Array(Color)

  def initialize
    @g = Classic.new(@e)
    @colors = [
    Color.new(0,0,0), Color.new(29,43,83), Color.new(126,37,83), Color.new(0,135,81),
    Color.new(171,82,54), Color.new(95,87,79), Color.new(194,195,199), Color.new(255,241,232),
    Color.new(255,0,77), Color.new(255,163,0), Color.new(255,236,39), Color.new(0,228,54),
    Color.new(41,173,255), Color.new(131,118,156), Color.new(255,119,168), Color.new(255,204,170)]
  end

  def key(key : KeyboardKey) : Bool
    Raylib.IsKeyDown(key)
  end

  def reset(seed : Int32)
    @e = Pico.new(seed)
    @g = Classic.new(@e)
    @g.begin_game
    @paused = false
  end

  def notify(message : String)
    @message = message
    @message_time = 90
  end

  def update
    @message_time -= 1 if @message_time > 0
    @paused = !@paused if Raylib.IsKeyPressed(KeyboardKey.P)
    if Raylib.IsKeyPressed(KeyboardKey.R)
      reset(1)
      @replaying = false
      @recording = false
    end
    begin
      if Raylib.IsKeyPressed(KeyboardKey.F5) && !@g.is_title
        Persistence.write(Persistence.path("progress"), Persistence.json(Persistence.capture(@g)))
        notify("CHECKPOINT SAVED")
      end
      if Raylib.IsKeyPressed(KeyboardKey.F9)
        save = Persistence.progress(File.ReadAllText(Persistence.path("progress")))
        reset(1)
        Persistence.restore(@g, save)
        @replaying = false
        @recording = false
        notify("CHECKPOINT LOADED")
      end
      if Raylib.IsKeyPressed(KeyboardKey.F6)
        if @recording
          Persistence.write(Persistence.path("replay"), Persistence.json(Replay.new(1, 1, @recorded.ToArray)))
          @recording = false
          notify("REPLAY SAVED")
        else
          reset(1)
          @recorded.Clear()
          @recording = true
          @replaying = false
          notify("RECORDING - F6 TO FINISH")
        end
      end
      if Raylib.IsKeyPressed(KeyboardKey.F7)
        replay = Persistence.replay(File.ReadAllText(Persistence.path("replay")))
        reset(replay.seed)
        @playback = replay.frames
        @play_index = 0
        @replaying = true
        @recording = false
        notify("PLAYING REPLAY")
      end
    rescue ex : Exception
      notify("FILE: #{ex.Message}")
    end
    return if @paused
    if @replaying
      if @play_index >= @playback.Length
        @replaying = false
        @paused = true
        notify("REPLAY FINISHED - P TO CONTINUE")
        return
      end
      @playback[@play_index].apply(@e)
      @play_index += 1
    else
      frame = InputFrame.new(key(KeyboardKey.Left) || key(KeyboardKey.A), key(KeyboardKey.Right) || key(KeyboardKey.D), key(KeyboardKey.Up) || key(KeyboardKey.W), key(KeyboardKey.Down) || key(KeyboardKey.S), key(KeyboardKey.Z) || key(KeyboardKey.Space) || key(KeyboardKey.C), key(KeyboardKey.X) || key(KeyboardKey.LeftShift))
      frame.apply(@e)
      if @recording
        @recorded.Add(frame)
        if @recorded.Count >= 108000
          Persistence.write(Persistence.path("replay"), Persistence.json(Replay.new(1, 1, @recorded.ToArray)))
          @recording = false
          notify("ONE HOUR REPLAY SAVED")
        end
      end
    end
    @g.update
    @g.draw
    @e.sounds.Clear()
  end

  def draw
    width = Raylib.GetScreenWidth()
    height = Raylib.GetScreenHeight()
    scale = Math.Max(1, Math.Min(width / 128, (height - 80) / 128))
    left = (width - scale * 128) / 2
    top = (height - scale * 128) / 2
    Raylib.ClearBackground(Color.new(13,16,28))
    # Runs of identical palette pixels are drawn together; no textures or native code in the game.
    for y in 0...128
      x = 0
      while x < 128
        color = @e.pixels[x + y * 128]
        run = x + 1
        while run < 128 && @e.pixels[run + y * 128] == color
          run += 1
        end
        Raylib.DrawRectangle(left + x * scale, top + y * scale, (run - x) * scale, scale, @colors[color])
        x = run
      end
    end
    Raylib.DrawText("CELESTE CLASSIC", left, Math.Max(4, top - 28), 18, @colors[7])
    status = @recording ? "REC" : (@replaying ? "REPLAY" : "#{@g.got_fruit.Count} BERRIES   #{@g.deaths} DEATHS")
    Raylib.DrawText(status, left + scale * 128 - Raylib.MeasureText(status, 14), Math.Max(6, top - 24), 14, @colors[14])
    Raylib.DrawText("Z JUMP  X DASH  P PAUSE  R RESTART", left, top + scale * 128 + 10, 12, @colors[6])
    footer = @message_time > 0 ? @message : "F5 SAVE  F9 LOAD  F6 RECORD  F7 REPLAY  ESC QUIT"
    Raylib.DrawText(footer, left, top + scale * 128 + 28, 12, @colors[13])
    if @paused
      Raylib.DrawRectangle(left, top + 52 * scale, 128 * scale, 24 * scale, Color.new(0,0,0,230))
      Raylib.DrawText("PAUSED", left + 48 * scale, top + 60 * scale, 3 * scale, @colors[7])
    end
  end

  def run
    Raylib.SetConfigFlags(ConfigFlags.ResizableWindow)
    Raylib.InitWindow(640, 720, "Celeste Classic - Alice")
    Raylib.SetWindowMinSize(512, 592)
    Raylib.SetTargetFPS(30)
    @g.draw
    begin
      until Raylib.WindowShouldClose()
        update
        Raylib.BeginDrawing()
        draw
        Raylib.EndDrawing()
      end
    ensure
      Raylib.CloseWindow()
    end
  end
end

Desktop.new.run
