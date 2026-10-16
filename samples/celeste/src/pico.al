# PICO-8 compatibility surface, rewritten from NoelFB/Celeste/Source/PICO-8/Emulator.cs.
# All drawing is into a managed 128x128 palette buffer; the game needs no window.
module Assets
  def self.root : String
    dir = AppContext.BaseDirectory
    while !String.IsNullOrEmpty(dir)
      path = Path.Combine(dir, "assets")
      return path if File.Exists(Path.Combine(path, "map.hex"))
      parent = Path.GetDirectoryName(dir)
      break if parent.nil?
      dir = parent
    end
    for path in ["assets", "celeste/assets", "samples/celeste/assets"]
      return Path.GetFullPath(path) if File.Exists(Path.Combine(path, "map.hex"))
    end
    raise "Cannot find celeste/assets/map.hex; run from the sample or samples workspace."
  end
end

record struct PixelSheet(width : Int32, height : Int32, data : Array(Int32))
  def self.load(name : String) : PixelSheet
    rows = File.ReadAllLines(Path.Combine(Assets.root, name + ".pixels"))
    width = rows[0].Length
    data = Array(Int32).new(width * rows.Length)
    for y in 0...rows.Length
      for x in 0...width
        c = rows[y][x]
        data[x + y * width] = c == '.' ? -1 : Convert.ToInt32(c.ToString, 16)
      end
    end
    PixelSheet.new(width, rows.Length, data)
  end
end

class Pico
  getter tiles : Array(Int32)
  getter pixels : Array(Int32) = Array(Int32).new(128 * 128)
  getter buttons : Array(Bool) = Array(Bool).new(6)
  getter sounds : List(Int32) = List(Int32).new
  property music_id : Int32 = -1
  @palette : Array(Int32) = Array(Int32).new(16)
  @atlas : PixelSheet = PixelSheet.load("atlas")
  @font : PixelSheet = PixelSheet.load("font")
  @random : Random
  @cx : Int32 = 0
  @cy : Int32 = 0

  MASK : Array(Int32) = [
  0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
  4,2,0,0,0,0,0,0,0,0,0,2,0,0,0,0,
  3,3,3,3,3,3,3,3,4,4,4,2,2,0,0,0,
  3,3,3,3,3,3,3,3,4,4,4,2,2,2,2,2,
  0,0,19,19,19,19,2,2,3,2,2,2,2,2,2,2,
  0,0,19,19,19,19,2,2,4,2,2,2,2,2,2,2,
  0,0,19,19,19,19,0,4,4,2,2,2,2,2,2,2,
  0,0,19,19,19,19,0,0,0,2,2,2,2,2,2,2]
  FONT_MAP : String = "abcdefghijklmnopqrstuvwxyz0123456789~!@#4%^&*()_+-=?:."

  def initialize(seed : Int32 = 1)
    @random = Random.new(seed)
    raw = String.Concat(File.ReadAllLines(Path.Combine(Assets.root, "map.hex")))
    @tiles = Array(Int32).new(raw.Length / 2)
    for i in 0...@tiles.Length
      a = raw[i * 2].ToString
      b = raw[i * 2 + 1].ToString
      # PICO-8 shares the bottom half of its map with sprite memory (swapped nibbles).
      @tiles[i] = Convert.ToInt32(i < @tiles.Length / 2 ? a + b : b + a, 16)
    end
    pal
  end

  def rnd(max : Float64) : Float64
    @random.NextDouble * max
  end

  def flr(n : Float64) : Int32
    Math.Floor(n).to_i
  end

  def sin(turn : Float64) : Float64
    Math.Sin((1.0 - turn) * Math.PI * 2.0)
  end

  def cos(turn : Float64) : Float64
    Math.Cos((1.0 - turn) * Math.PI * 2.0)
  end

  def btn(index : Int32) : Bool
    @buttons[index]
  end

  def dash_x(facing : Int32) : Int32
    x = btn(1) ? 1 : (btn(0) ? -1 : 0)
    x == 0 && !btn(2) && !btn(3) ? facing : x
  end

  def dash_y : Int32
    btn(3) ? 1 : (btn(2) ? -1 : 0)
  end

  def mget(tx : Int32, ty : Int32) : Int32
    tx < 0 || tx >= 128 || ty < 0 || ty >= 64 ? 0 : @tiles[tx + ty * 128]
  end

  def fget(tile : Int32, flag : Int32) : Bool
    tile >= 0 && tile < MASK.Length && (MASK[tile] & (1 << flag)) != 0
  end

  def sfx(id : Int32)
    @sounds.Add(id)
  end

  def music(id : Int32, fade : Int32 = 0, mask : Int32 = 7)
    @music_id = id
  end

  def camera(x : Float64 = 0.0, y : Float64 = 0.0)
    @cx = Math.Round(x).to_i
    @cy = Math.Round(y).to_i
  end

  def pal
    for i in 0...16
      @palette[i] = i
    end
  end

  def pal(a : Int32, b : Int32)
    @palette[a % 16] = b % 16
  end

  def pset(x : Int32, y : Int32, color : Int32)
    px = x - @cx
    py = y - @cy
    @pixels[px + py * 128] = @palette[color % 16] if px >= 0 && px < 128 && py >= 0 && py < 128
  end

  def rectfill(x : Float64, y : Float64, x2 : Float64, y2 : Float64, color : Float64)
    left = Math.Max(@cx, flr(Math.Min(x, x2)))
    top = Math.Max(@cy, flr(Math.Min(y, y2)))
    right = Math.Min(127 + @cx, flr(Math.Max(x, x2)))
    bottom = Math.Min(127 + @cy, flr(Math.Max(y, y2)))
    for py in top..bottom
      for px in left..right
        pset(px, py, color.to_i)
      end
    end
  end

  def circfill(x : Float64, y : Float64, r : Float64, color : Float64)
    if r <= 1.0
      rectfill(x - 1, y, x + 1, y, color)
      rectfill(x, y - 1, x, y + 1, color)
    elsif r <= 2.0
      rectfill(x - 2, y - 1, x + 2, y + 1, color)
      rectfill(x - 1, y - 2, x + 1, y + 2, color)
    else
      rectfill(x - 3, y - 1, x + 3, y + 1, color)
      rectfill(x - 1, y - 3, x + 1, y + 3, color)
      rectfill(x - 2, y - 2, x + 2, y + 2, color)
    end
  end

  def blit(sheet : PixelSheet, sx : Int32, sy : Int32, w : Int32, h : Int32, x : Int32, y : Int32, flip_x : Bool, flip_y : Bool, tint : Int32)
    for py in 0...h
      for px in 0...w
        ax = sx + (flip_x ? w - 1 - px : px)
        ay = sy + (flip_y ? h - 1 - py : py)
        if ax >= 0 && ax < sheet.width && ay >= 0 && ay < sheet.height
          c = sheet.data[ax + ay * sheet.width]
          pset(x + px, y + py, tint >= 0 ? tint : c) if c >= 0
        end
      end
    end
  end

  def spr(sprite : Float64, x : Float64, y : Float64, columns : Int32 = 1, rows : Int32 = 1, flip_x : Bool = false, flip_y : Bool = false)
    for sx in 0...columns
      for sy in 0...rows
        id = sprite.to_i + sx + sy * 16
        blit(@atlas, id % 16 * 8, id / 16 * 8, 8, 8, flr(x + sx * 8), flr(y + sy * 8), flip_x, flip_y, -1)
      end
    end
  end

  def print(text : String, x : Float64, y : Float64, color : Float64)
    for i in 0...text.Length
      index = FONT_MAP.IndexOf(text[i])
      if index >= 0
        blit(@font, index % 30 * 4, index / 30 * 6, 4, 6, flr(x) + i * 4, flr(y), false, false, color.to_i)
      end
    end
  end

  # Literal blocks become counted loops: room loading and all three map layers share this traversal.
  def each_tile(mx : Int32, my : Int32, w : Int32, h : Int32, &block : Int32, Int32, Int32 ->)
    for x in 0...w
      for y in 0...h
        yield x, y, mget(mx + x, my + y)
      end
    end
  end

  def map(mx : Int32, my : Int32, tx : Int32, ty : Int32, w : Int32, h : Int32, mask : Int32)
    each_tile(mx, my, w, h) do |x, y, tile|
      spr(tile.to_f, (tx + x * 8).to_f, (ty + y * 8).to_f) if fget(tile, mask)
    end
  end
end
