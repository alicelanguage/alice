# MonoGame drawing: everything is a tinted 1x1 white texture stretched into rectangles.
using Microsoft.Xna.Framework
using Microsoft.Xna.Framework.Graphics

class Painter
  getter batch : SpriteBatch
  getter pixel : Texture2D
  property ox : Float64 = 0.0
  property oy : Float64 = 0.0

  def initialize(@batch : SpriteBatch, @pixel : Texture2D)
  end

  def rect(x : Float64, y : Float64, w : Float64, h : Float64, c : Color)
    batch.Draw(pixel, Rectangle.new((x + ox).to_i, (y + oy).to_i, w.to_i, h.to_i), c)
  end

  def box(b : Box, c : Color)
    rect(b.x, b.y, b.w, b.h, c)
  end

  def text(s : String, x : Float64, y : Float64, scale : Int32, c : Color)
    Font.each_pixel(s) { |cx, cy| rect(x + cx * scale, y + cy * scale, scale.to_f, scale.to_f, c) }
  end

  def centered(s : String, y : Float64, scale : Int32, c : Color)
    text(s, (Rules::WIDTH - Font.width(s) * scale) / 2.0, y, scale, c)
  end
end

module Render
  def self.row_color(row : Float64) : Color
    case row.to_i
    when 0 then Color.Crimson
    when 1 then Color.OrangeRed
    when 2 then Color.Orange
    when 3 then Color.Gold
    when 4 then Color.LimeGreen
    else Color.DeepSkyBlue
    end
  end

  def self.world(p : Painter, w : World)
    p.ox = w.shake_offset_x
    p.oy = w.shake_offset_y
    p.rect(0.0, 0.0, Rules::WIDTH, 2.0, Color.DimGray)
    for i in 0...w.bricks.Length
      case w.bricks[i]
      when Brick::Solid(hp, row)
        c = row_color(row.to_f)
        c = Color.Lerp(c, Color.White, 0.25f32 * (hp - 1)) if hp > 1
        p.box(Rules.brick_box(i), c)
      when Brick::Empty
      end
    end
    p.box(w.paddle, Color.WhiteSmoke)
    p.box(w.ball.box, Color.White)
    w.particles.store.each do |s|
      a = Math.Clamp(s.life * 2.0, 0.0, 1.0).to_f32
      p.rect(s.x, s.y, 3.0, 3.0, row_color(s.hue) * a)
    end
    p.ox = 0.0
    p.oy = 0.0
    p.text("SCORE #{w.score}", 8.0, 12.0, 4, Color.White)
    p.text("LEVEL #{w.level}", 330.0, 12.0, 4, Color.White)
    p.text("LIVES #{w.lives}", 650.0, 12.0, 4, Color.White)
    overlay(p, w)
  end

  def self.overlay(p : Painter, w : World)
    case w.state
    when GameState::Title
      p.centered("BREAKOUT", 200.0, 12, Color.Gold)
      p.centered("PRESS ENTER OR SPACE", 330.0, 4, Color.White)
      p.centered("ARROWS A D OR MOUSE - P PAUSE", 370.0, 3, Color.LightGray)
    when GameState::Playing
      p.centered("SPACE OR CLICK TO LAUNCH", 420.0, 3, Color.LightGray) if w.ball.stuck
    when GameState::Paused
      p.centered("PAUSED", 260.0, 10, Color.White)
    when GameState::GameOver(s)
      p.centered("GAME OVER", 220.0, 10, Color.Crimson)
      p.centered("SCORE #{s} - ENTER TO RESTART", 330.0, 4, Color.White)
    when GameState::Won(s)
      p.centered("YOU WIN!", 220.0, 10, Color.LimeGreen)
      p.centered("SCORE #{s} - ENTER TO RESTART", 330.0, 4, Color.White)
    end
  end
end
