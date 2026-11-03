require "../src/pong"

def test_serve_starts_in_the_middle
  game = Pong.new
  assert_equal 395.0, game.ball_x
  assert_equal 220.0, game.ball_y
  assert game.vx > 0.0
end

def test_paddles_stay_on_screen
  game = Pong.new
  120.times { game.step(1.0 / 60.0, -1, 1) }
  assert_equal 0.0, game.left_y
  assert_equal (Pong::HEIGHT - Pong::PADDLE_H).to_f, game.right_y
end

def test_ball_bounces_off_the_top_wall
  game = Pong.new
  game.serve(-1)
  # moving up-left from the centre, the ball reaches the top wall before the left edge only if vy < 0
  assert game.vy < 0.0
  60.times { game.step(1.0 / 60.0, 0, 0) }
  assert game.ball_y >= 0.0
end

def test_a_missed_ball_scores_for_the_other_side
  game = Pong.new
  game.serve(-1)
  frames = 0
  while game.right_score == 0 && frames < 600
    game.step(1.0 / 60.0, 1, 0)                # left paddle runs to the bottom and misses
    frames += 1
  end
  assert_equal 1, game.right_score
  assert_equal 0, game.left_score
end

def test_a_paddle_hit_reverses_and_speeds_up_the_ball
  game = Pong.new
  speed = game.vx
  frames = 0
  while game.hits == 0 && frames < 600
    game.step(1.0 / 60.0, 0, Pong.follow(game.right_y.to_i, game.ball_center))
    frames += 1
  end
  assert_equal 1, game.hits
  assert game.vx < 0.0
  assert_equal speed * 1.05, -game.vx
end

def test_hit_zone_and_follow
  assert_equal 0, Pong.hit_zone(140, 100)
  assert_equal -4, Pong.hit_zone(90, 100)
  assert_equal 4, Pong.hit_zone(200, 100)
  assert_equal 1, Pong.follow(100, 200)
  assert_equal -1, Pong.follow(100, 50)
  assert_equal 0, Pong.follow(100, 145)
end

def test_pure_helpers_match_the_constants
  assert_equal Pong::HEIGHT - Pong::PADDLE_H, Pong.clamp_paddle(100000)
  assert_equal 0, Pong.hit_zone(Pong::PADDLE_H / 2, 0)
  assert_equal 4, Pong.hit_zone(Pong::PADDLE_H, 0)
end
