# MonoGame in Alice: run with `alice run` (needs a display).
using Microsoft.Xna.Framework
using Microsoft.Xna.Framework.Graphics
using Microsoft.Xna.Framework.Input

class BounceGame < Game
  @graphics : GraphicsDeviceManager
  @batch : SpriteBatch? = nil
  @pixel : Texture2D? = nil
  @pos : Vector2 = Vector2.new(100.0f32, 100.0f32)
  @vel : Vector2 = Vector2.new(220.0f32, 160.0f32)

  def initialize
    @graphics = GraphicsDeviceManager.new(self)
    self.IsMouseVisible = true
    self.Window.Title = "Bounce — made with Alice"
  end

  def LoadContent
    @batch = SpriteBatch.new(GraphicsDevice)
    px = Texture2D.new(GraphicsDevice, 1, 1)
    px.SetData([Color.White])
    @pixel = px
  end

  def Update(gameTime : GameTime)
    Exit() if Keyboard.GetState.IsKeyDown(Keys.Escape)
    dt = gameTime.ElapsedGameTime.TotalSeconds.to_f32
    @pos += @vel * dt
    w = GraphicsDevice.Viewport.Width.to_f32 - 40.0f32
    h = GraphicsDevice.Viewport.Height.to_f32 - 40.0f32
    @vel = Vector2.new(-@vel.X, @vel.Y) if @pos.X < 0.0f32 || @pos.X > w
    @vel = Vector2.new(@vel.X, -@vel.Y) if @pos.Y < 0.0f32 || @pos.Y > h
    super
  end

  def Draw(gameTime : GameTime)
    GraphicsDevice.Clear(Color.CornflowerBlue)
    b = @batch
    px = @pixel
    unless b.nil? || px.nil?
      b.Begin
      b.Draw(px, Rectangle.new(@pos.X.to_i, @pos.Y.to_i, 40, 40), Color.Orange)
      b.End
    end
    super
  end
end

game = BounceGame.new
game.Run
game.Dispose
