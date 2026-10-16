# Pure Breakout rules: states, bricks, score, lives, levels. Driven by an Input snapshot.

enum GameState
  Title
  Playing
  Paused
  GameOver(final_score : Int32)
  Won(final_score : Int32)
end

enum Brick
  Solid(hp : Int32, row : Int32)
  Empty
end

# One frame of input; edge-triggered keys (pressed this frame) are computed by the app.
record Input(left : Bool, right : Bool, mouse_x : Float64, mouse_moved : Bool, launch : Bool, pause : Bool, enter : Bool)

module Rules
  WIDTH : Float64 = 800.0
  HEIGHT : Float64 = 600.0
  COLS : Int32 = 10
  ROWS : Int32 = 6
  BRICK_W : Float64 = 76.0
  BRICK_H : Float64 = 22.0
  BRICK_GAP : Float64 = 4.0
  TOP : Float64 = 70.0
  PADDLE_SPEED : Float64 = 520.0
  MAX_LEVEL : Int32 = 3
  START_LIVES : Int32 = 3

  def self.ball_speed(level : Int32) : Float64
    300.0 + (level - 1) * 70.0
  end

  def self.brick_box(i : Int32) : Box
    col = i % COLS
    row = i / COLS
    Box.new(BRICK_GAP + col * (BRICK_W + BRICK_GAP), TOP + row * (BRICK_H + BRICK_GAP), BRICK_W, BRICK_H)
  end

  # Level layouts: 1 = full wall, 2 = tough top rows, 3 = checkerboard of 3-hit bricks.
  def self.layout(level : Int32) : Array(Brick)
    bricks = Array(Brick).new(COLS * ROWS)
    for i in 0...(COLS * ROWS)
      col = i % COLS
      row = i / COLS
      bricks[i] = case level
      when 1 then Brick::Solid(1, row)
      when 2 then Brick::Solid(row < 2 ? 2 : 1, row)
      else
        (col + row) % 2 == 0 ? Brick::Solid(3, row) : Brick::Solid(1, row)
      end
    end
    bricks
  end

  def self.points(row : Int32, level : Int32) : Int32
    (ROWS - row) * 10 * level
  end
end

class World
  getter state : GameState = GameState::Title
  getter score : Int32 = 0
  getter lives : Int32 = 3
  getter level : Int32 = 1
  getter paddle : Box = Box.new(350.0, 560.0, 100.0, 14.0)
  getter ball : Ball = Ball.new(395.0, 546.0)
  getter bricks : Array(Brick) = Rules.layout(1)
  getter particles : Particles = Particles.new
  getter shake : Float64 = 0.0
  @rng : Random = Random.new(42)

  def state_name : String
    case state
    when GameState::Title then "title"
    when GameState::Playing then "playing"
    when GameState::Paused then "paused"
    when GameState::GameOver(_s) then "game over"
    when GameState::Won(_s) then "won"
    end
  end

  def bricks_left : Int32
    bricks.count { |b| b != Brick::Empty }
  end

  # Fresh game, straight into Playing.
  def start
    @score = 0
    @lives = Rules::START_LIVES
    @level = 1
    @bricks = Rules.layout(1)
    @particles.clear
    reset_ball
    @state = GameState::Playing
  end

  def reset_ball
    @paddle = Box.new((Rules::WIDTH - paddle.w) / 2.0, paddle.y, paddle.w, paddle.h)
    @ball = Ball.new(paddle.center_x - 5.0, paddle.y - 14.0)
  end

  def launch
    return unless ball.stuck
    ball.stuck = false
    s = Rules.ball_speed(level)
    ball.vx = s * 0.5
    ball.vy = -s * 0.866
  end

  def update(dt : Float64, input : Input)
    @shake = Math.Max(0.0, shake - dt)
    @particles.update(dt)
    case state
    when GameState::Title
      start if input.enter || input.launch
    when GameState::Playing
      if input.pause
        @state = GameState::Paused
      else
        play(dt, input)
      end
    when GameState::Paused
      @state = GameState::Playing if input.pause
    when GameState::GameOver(_s)
      start if input.enter
    when GameState::Won(_s)
      start if input.enter
    end
  end

  # Screen-shake offset in pixels for this frame.
  def shake_offset_x : Float64
    shake > 0.0 ? (@rng.NextDouble - 0.5) * shake * 40.0 : 0.0
  end

  def shake_offset_y : Float64
    shake > 0.0 ? (@rng.NextDouble - 0.5) * shake * 40.0 : 0.0
  end

  def play(dt : Float64, input : Input)
    move_paddle(dt, input)
    if ball.stuck
      ball.x = paddle.center_x - ball.size / 2.0
      ball.y = paddle.y - ball.size - 4.0
      launch if input.launch
      return
    end
    ball.step(dt)
    if Physics.walls(ball, Rules::WIDTH, Rules::HEIGHT)
      lose_life
      return
    end
    Physics.paddle(ball, paddle)
    hit_bricks
    next_level if bricks_left == 0
  end

  def move_paddle(dt : Float64, input : Input)
    x = paddle.x
    if input.mouse_moved
      x = input.mouse_x - paddle.w / 2.0
    end
    x -= Rules::PADDLE_SPEED * dt if input.left
    x += Rules::PADDLE_SPEED * dt if input.right
    x = Math.Clamp(x, 0.0, Rules::WIDTH - paddle.w)
    @paddle = Box.new(x, paddle.y, paddle.w, paddle.h)
  end

  # At most one brick per frame keeps the reflection stable.
  def hit_bricks
    for i in 0...bricks.Length
      b = Rules.brick_box(i)
      hit = case bricks[i]
      when Brick::Solid(hp, row)
        if Physics.reflect(ball, b)
          damage(i, hp, row, b)
          true
        else
          false
        end
      when Brick::Empty then false
      end
      break if hit
    end
  end

  def damage(i : Int32, hp : Int32, row : Int32, b : Box)
    if hp > 1
      @bricks[i] = Brick::Solid(hp - 1, row)
      @shake = 0.08
    else
      @bricks[i] = Brick::Empty
      @score += Rules.points(row, level)
      @particles.burst(b.center_x, b.center_y, row.to_f, 14)
      @shake = 0.15
    end
  end

  def lose_life
    @lives -= 1
    @shake = 0.4
    if lives <= 0
      @state = GameState::GameOver(score)
    else
      reset_ball
    end
  end

  def next_level
    if level >= Rules::MAX_LEVEL
      @state = GameState::Won(score)
      return
    end
    @level += 1
    @bricks = Rules.layout(level)
    reset_ball
  end
end
