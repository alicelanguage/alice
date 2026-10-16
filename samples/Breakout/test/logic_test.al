# Headless checks of the pure game logic (src/ has no MonoGame types).

def idle : Input
  Input.new(false, false, 0.0, false, false, false, false)
end

def press_enter : Input
  Input.new(false, false, 0.0, false, false, false, true)
end

def press_pause : Input
  Input.new(false, false, 0.0, false, false, true, false)
end

def test_box_overlap
  a = Box.new(0.0, 0.0, 10.0, 10.0)
  assert a.overlaps?(Box.new(5.0, 5.0, 10.0, 10.0))
  assert !a.overlaps?(Box.new(10.0, 0.0, 10.0, 10.0))
  assert !a.overlaps?(Box.new(0.0, 20.0, 10.0, 10.0))
end

def test_wall_bounce_and_fall
  b = Ball.new(-2.0, 100.0)
  b.vx = -50.0
  assert !Physics.walls(b, 800.0, 600.0)
  assert b.vx > 0.0
  b.y = 700.0
  assert Physics.walls(b, 800.0, 600.0)
end

def test_paddle_bounce_angle
  p = Box.new(100.0, 500.0, 100.0, 10.0)
  b = Ball.new(180.0, 495.0)
  b.vx = 0.0
  b.vy = 300.0
  assert Physics.paddle(b, p)
  assert b.vy < 0.0
  assert b.vx > 0.0
  assert Math.Abs(b.speed - 300.0) < 0.001
end

def test_brick_reflect_vertical
  b = Ball.new(20.0, 28.0)
  b.vy = -200.0
  assert Physics.reflect(b, Box.new(0.0, 0.0, 100.0, 30.0))
  assert b.vy > 0.0
end

def test_title_to_playing_and_pause
  w = World.new
  assert_equal "title", w.state_name
  w.update(0.016, press_enter)
  assert_equal "playing", w.state_name
  assert_equal 3, w.lives
  w.update(0.016, press_pause)
  assert_equal "paused", w.state_name
  w.update(0.016, press_pause)
  assert_equal "playing", w.state_name
end

def test_breaking_brick_scores_and_spawns_particles
  w = World.new
  w.start
  bx = Rules.brick_box(Rules::COLS * (Rules::ROWS - 1))
  w.ball.stuck = false
  w.ball.x = bx.x + 10.0
  w.ball.y = bx.bottom + 1.0
  w.ball.vx = 0.0
  w.ball.vy = -300.0
  before = w.bricks_left
  w.update(0.016, idle)
  assert_equal before - 1, w.bricks_left
  assert_equal Rules.points(Rules::ROWS - 1, 1), w.score
  assert w.particles.size > 0
  assert w.shake > 0.0
end

def test_particles_expire
  ps = Particles.new
  ps.burst(10.0, 10.0, 1.0, 20)
  assert_equal 20, ps.size
  5.times { |i| ps.update(0.25) }
  assert_equal 0, ps.size
end

def test_losing_all_lives_is_game_over
  w = World.new
  w.start
  3.times do |i|
    w.ball.stuck = false
    w.ball.y = 700.0
    w.ball.vy = 100.0
    w.update(0.016, idle)
  end
  assert_equal 0, w.lives
  assert_equal "game over", w.state_name
  w.update(0.016, press_enter)
  assert_equal "playing", w.state_name
  assert_equal 3, w.lives
end

def test_clearing_bricks_advances_level_then_wins
  w = World.new
  w.start
  for lv in 1..3
    for i in 0...w.bricks.Length
      w.bricks[i] = Brick::Empty
    end
    w.ball.stuck = false
    w.ball.x = 400.0
    w.ball.y = 300.0
    w.ball.vy = -10.0
    w.update(0.016, idle)
  end
  assert_equal 3, w.level
  assert_equal "won", w.state_name
  assert Rules.ball_speed(3) > Rules.ball_speed(1)
end

def test_font
  assert_equal 5, Font.glyph("a").Length
  assert_equal 11, Font.width("ABC")
  assert_equal 12, Font.pixel_count("0")
  assert_equal 0, Font.pixel_count(" ")
end
