# Breakout entry point: MonoGame wiring only; rules live in src/ (pure, tested headless).
using Microsoft.Xna.Framework
using Microsoft.Xna.Framework.Graphics
using Microsoft.Xna.Framework.Input

class BreakoutGame < Game
  @graphics : GraphicsDeviceManager
  @painter : Painter? = nil
  @world : World = World.new
  @prev_keys : KeyboardState = KeyboardState.new
  @prev_mouse : MouseState = MouseState.new
  # Demo capture (README GIF): ALICE_BREAKOUT_AUTOPILOT=1 plays itself; ALICE_BREAKOUT_RECORD=FILE also
  # writes ALICE_BREAKOUT_FRAMES back-buffer frames as raw RGBA (WIDTHxHEIGHT) to FILE, then quits.
  @autopilot : Bool = Environment.GetEnvironmentVariable("ALICE_BREAKOUT_AUTOPILOT") == "1"
  @record : String? = Environment.GetEnvironmentVariable("ALICE_BREAKOUT_RECORD")
  @frames_left : Int32 = (Environment.GetEnvironmentVariable("ALICE_BREAKOUT_FRAMES") || "600").to_i
  @clock : Float64 = 0.0
  @out : FileStream? = nil

  def initialize
    @graphics = GraphicsDeviceManager.new(self)
    @graphics.PreferredBackBufferWidth = Rules::WIDTH.to_i
    @graphics.PreferredBackBufferHeight = Rules::HEIGHT.to_i
    self.IsMouseVisible = true
    self.Window.Title = "Breakout - made with Alice"
  end

  def LoadContent
    px = Texture2D.new(GraphicsDevice, 1, 1)
    px.SetData([Color.White])
    @painter = Painter.new(SpriteBatch.new(GraphicsDevice), px)
  end

  def pressed?(k : KeyboardState, key : Keys) : Bool
    k.IsKeyDown(key) && !@prev_keys.IsKeyDown(key)
  end

  def Update(gameTime : GameTime)
    k = Keyboard.GetState
    m = Mouse.GetState
    Exit() if k.IsKeyDown(Keys.Escape)
    dt = Math.Min(gameTime.ElapsedGameTime.TotalSeconds, 0.05)
    clicked = m.LeftButton == ButtonState.Pressed && @prev_mouse.LeftButton == ButtonState.Released
    input = Input.new(k.IsKeyDown(Keys.Left) || k.IsKeyDown(Keys.A), k.IsKeyDown(Keys.Right) || k.IsKeyDown(Keys.D), m.X.to_f, m.X != @prev_mouse.X, pressed?(k, Keys.Space) || clicked, pressed?(k, Keys.P), pressed?(k, Keys.Enter))
    if @autopilot || !@record.nil?
      # follow the ball, hitting it off-center so the angles vary
      @clock += dt
      aim = @world.ball.x + Math.Sin(@clock * 1.7) * 38.0
      input = Input.new(false, false, aim, true, true, false, true)
    end
    @world.update(dt, input)
    @prev_keys = k
    @prev_mouse = m
    super
  end

  def Draw(gameTime : GameTime)
    GraphicsDevice.Clear(Color.new(16, 18, 32))
    p = @painter
    unless p.nil?
      p.batch.Begin
      Render.world(p, @world)
      p.batch.End
    end
    capture
    super
  end

  def capture
    path = @record
    return if path.nil?
    if @out.nil?
      self.IsFixedTimeStep = true
      @out = File.Create(path)
    end
    data = Array(Color).new(Rules::WIDTH.to_i * Rules::HEIGHT.to_i, Color.Black)
    GraphicsDevice.GetBackBufferData(data)
    bytes = Array(Byte).new(data.size * 4, 0_u8)
    data.each_with_index do |c, i|
      bytes[i * 4] = c.R
      bytes[i * 4 + 1] = c.G
      bytes[i * 4 + 2] = c.B
      bytes[i * 4 + 3] = c.A
    end
    f = @out
    f.Write(bytes, 0, bytes.size) unless f.nil?
    @frames_left -= 1
    if @frames_left <= 0
      f.Dispose unless f.nil?
      Exit()
    end
  end
end

game = BreakoutGame.new
game.Run
game.Dispose
