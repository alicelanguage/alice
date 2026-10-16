# Game of Life in the terminal.
#   alice run                         animate (Ctrl-C to stop)
#   alice run -- --frames 120 --export frames   write PPM frames (turn into a GIF with ImageMagick)
require "./life"

GLIDER_GUN = [
  "........................#...........",
  "......................#.#...........",
  "............##......##............##",
  "...........#...#....##............##",
  "##........#.....#...##..............",
  "##........#...#.##....#.#...........",
  "..........#.....#.......#...........",
  "...........#...#....................",
  "............##......................",
]

def board : Life
  life = Life.new(48, 24)
  GLIDER_GUN.each_with_index do |row, y|
    row.each_char_with_index { |c, x| life.set(x + 2, y + 2, c == '#') }
  end
  life
end

frames = ARGV.includes?("--frames") ? Int32.Parse(ARGV[ARGV.index("--frames").not_nil! + 1]) : 0
export = ARGV.includes?("--export") ? ARGV[ARGV.index("--export").not_nil! + 1] : ""
life = board

if export != ""
  Directory.CreateDirectory(export)
  for f in 0...frames
    life.write_ppm(Path.Combine(export, "frame#{f.ToString("D4")}.ppm"), 8)
    life.step
  end
  puts "wrote #{frames} frames to #{export}/"
else
  n = 0
  while frames == 0 || n < frames
    print "\e[H\e[2J"
    puts life.to_s
    puts "generation #{life.generation} · population #{life.population}"
    life.step
    sleep 0.06
    n += 1
  end
end
