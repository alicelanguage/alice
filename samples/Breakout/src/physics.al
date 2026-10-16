# Pure AABB physics: no MonoGame types, so `alice test` can check it headless.

record struct Box(x : Float64, y : Float64, w : Float64, h : Float64)
  def right : Float64
    x + w
  end

  def bottom : Float64
    y + h
  end

  def center_x : Float64
    x + w / 2.0
  end

  def center_y : Float64
    y + h / 2.0
  end

  def overlaps?(o : Box) : Bool
    x < o.right && right > o.x && y < o.bottom && bottom > o.y
  end
end

class Ball
  property x : Float64
  property y : Float64
  property vx : Float64 = 0.0
  property vy : Float64 = 0.0
  property stuck : Bool = true

  def initialize(@x : Float64, @y : Float64)
  end

  def size : Float64
    10.0
  end

  def box : Box
    Box.new(x, y, size, size)
  end

  def speed : Float64
    Math.Sqrt(vx * vx + vy * vy)
  end

  def step(dt : Float64)
    @x += vx * dt
    @y += vy * dt
  end

  # Keep direction, change magnitude.
  def set_speed(s : Float64)
    cur = speed
    return if cur <= 0.0
    @vx = vx / cur * s
    @vy = vy / cur * s
  end
end

module Physics
  # Bounce off left/right/top walls. Returns true when the ball fell out the bottom.
  def self.walls(ball : Ball, width : Float64, height : Float64) : Bool
    if ball.x < 0.0
      ball.x = 0.0
      ball.vx = Math.Abs(ball.vx)
    elsif ball.x + ball.size > width
      ball.x = width - ball.size
      ball.vx = -Math.Abs(ball.vx)
    end
    if ball.y < 0.0
      ball.y = 0.0
      ball.vy = Math.Abs(ball.vy)
    end
    ball.y > height
  end

  # Paddle: outgoing angle depends on where the ball hits (-60..60 degrees).
  def self.paddle(ball : Ball, paddle : Box) : Bool
    return false unless ball.vy > 0.0 && ball.box.overlaps?(paddle)
    offset = (ball.box.center_x - paddle.center_x) / (paddle.w / 2.0)
    offset = Math.Clamp(offset, -1.0, 1.0)
    angle = offset * Math::PI / 3.0
    s = ball.speed
    ball.vx = s * Math.Sin(angle)
    ball.vy = -s * Math.Cos(angle)
    ball.y = paddle.y - ball.size
    true
  end

  # Ball vs static box: push out along the axis of least penetration and reflect.
  def self.reflect(ball : Ball, b : Box) : Bool
    a = ball.box
    return false unless a.overlaps?(b)
    pen_x = Math.Min(a.right - b.x, b.right - a.x)
    pen_y = Math.Min(a.bottom - b.y, b.bottom - a.y)
    if pen_x < pen_y
      if a.center_x < b.center_x
        ball.x -= pen_x
        ball.vx = -Math.Abs(ball.vx)
      else
        ball.x += pen_x
        ball.vx = Math.Abs(ball.vx)
      end
    else
      if a.center_y < b.center_y
        ball.y -= pen_y
        ball.vy = -Math.Abs(ball.vy)
      else
        ball.y += pen_y
        ball.vy = Math.Abs(ball.vy)
      end
    end
    true
  end
end
