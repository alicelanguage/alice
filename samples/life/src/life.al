# Conway's Game of Life on a torus. Cells are bytes in one flat array; each generation is written into a
# second buffer, then the buffers swap (no per-step allocation).
class Life
  getter width : Int32
  getter height : Int32
  getter generation : Int32 = 0
  @cells : Array(UInt8)
  @next : Array(UInt8)

  def initialize(@width : Int32, @height : Int32)
    @cells = Array(UInt8).new(width * height)
    @next = Array(UInt8).new(width * height)
  end

  def self.parse(rows : Array(String)) : Life
    life = Life.new(rows.map { |r| r.size }.max, rows.size)
    rows.each_with_index do |row, y|
      row.each_char_with_index { |c, x| life.set(x, y, c == '#' || c == 'O') }
    end
    life
  end

  def alive?(x : Int32, y : Int32) : Bool
    @cells[wrap(y, @height) * @width + wrap(x, @width)] == 1
  end

  def set(x : Int32, y : Int32, on : Bool)
    @cells[y * @width + x] = on ? 1_u8 : 0_u8
  end

  def population : Int32
    n = 0
    for i in 0...@cells.Length
      n += @cells[i]
    end
    n
  end

  def neighbours(x : Int32, y : Int32) : Int32
    n = 0
    for dy in -1..1
      for dx in -1..1
        n += 1 if (dx != 0 || dy != 0) && alive?(x + dx, y + dy)
      end
    end
    n
  end

  def step
    w = @width
    src = @cells
    dst = @next
    for y in 0...@height
      for x in 0...w
        n = neighbours(x, y)
        here = src[y * w + x] == 1
        dst[y * w + x] = n == 3 || (here && n == 2) ? 1_u8 : 0_u8
      end
    end
    @cells = dst
    @next = src
    @generation += 1
  end

  def to_s : String
    String.Join("\n", (0...@height).map { |y| String.new((0...@width).map { |x| alive?(x, y) ? '█' : '·' }.to_a.ToArray) })
  end

  # One frame as a binary PPM (P6): cells become `scale`-pixel squares in the Alice palette.
  def write_ppm(path : String, scale : Int32)
    w = @width * scale
    h = @height * scale
    header = System.Text.Encoding.ASCII.GetBytes("P6\n#{w} #{h}\n255\n")
    px = Array(UInt8).new(w * h * 3)
    for y in 0...h
      for x in 0...w
        on = alive?(x / scale, y / scale)
        i = (y * w + x) * 3
        px[i] = on ? 245_u8 : 20_u8
        px[i + 1] = on ? 239_u8 : 18_u8
        px[i + 2] = on ? 224_u8 : 16_u8
      end
    end
    File.WriteAllBytes(path, header.Concat(px).ToArray)
  end

  private def wrap(v : Int32, n : Int32) : Int32
    m = v % n
    m < 0 ? m + n : m
  end
end
