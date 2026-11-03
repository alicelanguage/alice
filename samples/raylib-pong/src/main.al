# Pong on raylib (Raylib-cs). W/S or Up/Down move the left paddle; the right paddle plays itself.
#
#   alice run                      open the window (needs a display)
#   alice run -- --simulate 3600   play 3600 frames (60 s) with no window and print the score
using Raylib_cs
require "./pong"

def simulate(frames : Int32)
  game = Pong.new
  for i in 0...frames
    left = Pong.follow(game.left_y.to_i, game.ball_center)
    right = Pong.follow(game.right_y.to_i, game.ball_center)
    game.step(1.0 / 60.0, left, right, 0.3)    # the left player is slower, so points are scored
  end
  puts "after #{frames} frames: left #{game.left_score}, right #{game.right_score}, paddle hits #{game.hits}"
end

def key?(k : KeyboardKey) : Bool
  Raylib.IsKeyDown(k)     # raylib returns CBool; this converts it to Bool
end

def play
  Raylib.InitWindow(Pong::WIDTH, Pong::HEIGHT, "Alice Pong")
  Raylib.SetTargetFPS(60)
  game = Pong.new
  while !Raylib.WindowShouldClose()
    up = key?(KeyboardKey.W) || key?(KeyboardKey.Up)
    down = key?(KeyboardKey.S) || key?(KeyboardKey.Down)
    left = up ? -1 : down ? 1 : 0
    game.step(Raylib.GetFrameTime().to_f, left, Pong.follow(game.right_y.to_i, game.ball_center))
    Raylib.BeginDrawing()
    Raylib.ClearBackground(Color.Black)
    for y in 0...(Pong::HEIGHT / 30)
      Raylib.DrawRectangle(Pong::WIDTH / 2 - 2, y * 30 + 8, 4, 14, Color.DarkGray)
    end
    Raylib.DrawRectangle(0, game.left_y.to_i, Pong::PADDLE_W, Pong::PADDLE_H, Color.White)
    Raylib.DrawRectangle(Pong::WIDTH - Pong::PADDLE_W, game.right_y.to_i, Pong::PADDLE_W, Pong::PADDLE_H, Color.White)
    Raylib.DrawRectangle(game.ball_x.to_i, game.ball_y.to_i, Pong::BALL, Pong::BALL, Color.Gold)
    Raylib.DrawText("#{game.left_score}", Pong::WIDTH / 2 - 60, 20, 40, Color.White)
    Raylib.DrawText("#{game.right_score}", Pong::WIDTH / 2 + 40, 20, 40, Color.White)
    Raylib.EndDrawing()
  end
  Raylib.CloseWindow()
end

at = ARGV.index("--simulate")
if at.nil?
  play
else
  simulate(ARGV.size > at.not_nil! + 1 ? Int32.Parse(ARGV[at.not_nil! + 1]) : 3600)
end
