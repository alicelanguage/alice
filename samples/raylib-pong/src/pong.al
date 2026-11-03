# Pong rules with no graphics: the window code (main.al) only reads keys and draws this state, so every rule
# here runs under `alice test` and the integer helpers are proven in RULES.al.
class Pong
  WIDTH = 800
  HEIGHT = 450
  PADDLE_W = 12
  PADDLE_H = 80
  BALL = 10
  PADDLE_SPEED = 360.0      # pixels per second
  SERVE_SPEED = 300.0
  MAX_SPEED = 900.0

  getter left_y : Float64 = 185.0
  getter right_y : Float64 = 185.0
  getter ball_x : Float64 = 0.0
  getter ball_y : Float64 = 0.0
  getter vx : Float64 = 0.0
  getter vy : Float64 = 0.0
  getter left_score : Int32 = 0
  getter right_score : Int32 = 0
  getter hits : Int32 = 0

  def initialize
    serve(1)
  end

  # Ball to the centre, moving towards `direction` (1 = right, -1 = left).
  def serve(direction : Int32)
    @ball_x = (WIDTH - BALL) / 2.0
    @ball_y = (HEIGHT - BALL) / 2.0
    @vx = SERVE_SPEED * direction
    @vy = 120.0 * direction
  end

  # The pure helpers below spell the sizes as numbers (370 = HEIGHT - PADDLE_H, 40 = PADDLE_H / 2): a `pure def`
  # cannot read a constant yet (AL0210). test_pure_helpers_match_the_constants keeps them in step.

  # Keeps a paddle's top edge on screen.
  pure def self.clamp_paddle(y : Int32) : Int32
    Math.Min(Math.Max(y, 0), 370)
  end

  # Where on the paddle the ball hit, from -4 (top edge) to 4 (bottom edge): steers the bounce.
  # Alice's `/` rounds down (-1 / 40 == -1), so the zone is computed on the distance and the sign put back;
  # `offset * 4 / 40` was lopsided, which RULES.al found (hit_zone_is_bounded_and_symmetric).
  pure def self.hit_zone(ball_center : Int32, paddle_top : Int32) : Int32
    offset = ball_center - (paddle_top + 40)
    zone = Math.Min(offset.abs * 4 / 40, 4)
    offset < 0 ? -zone : zone
  end

  # A simple opponent: move towards the ball, with a dead zone so it does not jitter. -1 up, 0 stay, 1 down.
  pure def self.follow(paddle_top : Int32, ball_center : Int32) : Int32
    center = paddle_top + 40
    if ball_center < center - 8
      -1
    elsif ball_center > center + 8
      1
    else
      0
    end
  end

  # One frame. `left` and `right` are -1 (up), 0 or 1 (down); `speed` scales the left paddle (an easier opponent).
  def step(dt : Float64, left : Int32, right : Int32, left_speed : Float64 = 1.0)
    @left_y = Pong.clamp_paddle((@left_y + left * PADDLE_SPEED * left_speed * dt).to_i).to_f
    @right_y = Pong.clamp_paddle((@right_y + right * PADDLE_SPEED * dt).to_i).to_f
    @ball_x += @vx * dt
    @ball_y += @vy * dt
    if @ball_y < 0.0
      @ball_y = -@ball_y
      @vy = -@vy
    elsif @ball_y > (HEIGHT - BALL).to_f
      @ball_y = 2.0 * (HEIGHT - BALL) - @ball_y
      @vy = -@vy
    end
    if @vx < 0.0 && @ball_x <= PADDLE_W.to_f && overlaps(@left_y)
      bounce(@left_y, PADDLE_W.to_f)
    elsif @vx > 0.0 && @ball_x + BALL >= (WIDTH - PADDLE_W).to_f && overlaps(@right_y)
      bounce(@right_y, (WIDTH - PADDLE_W - BALL).to_f)
    end
    if @ball_x + BALL < 0.0
      @right_score += 1
      serve(1)
    elsif @ball_x > WIDTH.to_f
      @left_score += 1
      serve(-1)
    end
  end

  def overlaps(paddle_top : Float64) : Bool
    @ball_y + BALL >= paddle_top && @ball_y <= paddle_top + PADDLE_H
  end

  def bounce(paddle_top : Float64, x : Float64)
    @hits += 1
    @ball_x = x
    @vx = Math.Clamp(-@vx * 1.05, -MAX_SPEED, MAX_SPEED)
    @vy = Math.Clamp(@vy + Pong.hit_zone((@ball_y + BALL / 2.0).to_i, paddle_top.to_i) * 60.0, -420.0, 420.0)
  end

  def ball_center : Int32
    (@ball_y + BALL / 2.0).to_i
  end
end
