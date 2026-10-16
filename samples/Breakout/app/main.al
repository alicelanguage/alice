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
    super
  end
end

game = BreakoutGame.new
game.Run
game.Dispose
