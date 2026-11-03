# Properties of the Pong rules, checked on every case of their domains by `alice prove` (written to PROOF.al).
# Sizes are numbers here for the same reason as in src/pong.al: 450 = HEIGHT, 80 = PADDLE_H.
require "./src/pong"

# Whatever the input, a paddle's top edge stays on screen and the paddle fits below it.
rule paddle_stays_on_screen(y in -1000..1000)
  c = Pong.clamp_paddle(y)
  c >= 0 && c + 80 <= 450
end

# Clamping is idempotent and leaves on-screen positions alone.
rule clamp_is_stable(y in -1000..1000)
  c = Pong.clamp_paddle(y)
  Pong.clamp_paddle(c) == c && (y < 0 || y > 370 || c == y)
end

# The bounce zone is always -4..4 and symmetric around the paddle centre.
rule hit_zone_is_bounded_and_symmetric(top in 0..370, d in 0..60)
  center = top + 40
  a = Pong.hit_zone(center + d, top)
  b = Pong.hit_zone(center - d, top)
  a >= 0 && a <= 4 && b == -a
end

# The opponent never moves away from the ball.
rule follow_moves_towards_the_ball(top in 0..370, ball in 0..450)
  center = top + 40
  m = Pong.follow(top, ball)
  (m == -1 && ball < center) || (m == 1 && ball > center) || (m == 0 && (ball - center).abs <= 8)
end
