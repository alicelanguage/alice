# Mandelbrot renderer: smooth (continuous) iteration counts mapped onto an ink → cherry → ivory palette.
# Rows render in parallel with Par.Map (each row is its own array: no shared writes, race-free by construction).
module Fractal
  # Smooth escape count at c = (cx, cy); max_iter when inside the set.
  def self.escape(cx : Float64, cy : Float64, max_iter : Int32) : Float64
    zr = 0.0
    zi = 0.0
    i = 0
    while i < max_iter && zr * zr + zi * zi <= 256.0
      t = zr * zr - zi * zi + cx
      zi = 2.0 * zr * zi + cy
      zr = t
      i += 1
    end
    return max_iter.to_f if i >= max_iter
    i + 1.0 - Math.Log2(Math.Log(Math.Sqrt(zr * zr + zi * zi)))
  end

  def self.palette(t : Float64) : {Float64, Float64, Float64}
    # ink (0) → cherry (0.45) → ivory (1), cycling
    u = t - Math.Floor(t)
    if u < 0.45
      k = u / 0.45
      {0.08 + 0.68 * k, 0.07 + 0.02 * k, 0.06 + 0.18 * k}
    else
      k = (u - 0.45) / 0.55
      {0.76 + 0.20 * k, 0.09 + 0.85 * k, 0.24 + 0.64 * k}
    end
  end

  def self.render(cx : Float64, cy : Float64, scale : Float64, w : Int32, h : Int32, max_iter : Int32) : Array(UInt8)
    rows = Par.Map((0...h).to_a.ToArray) do |y|
      row = Array(UInt8).new(w * 3)
      for x in 0...w
        n = escape(cx + (x - w / 2.0) * scale, cy + (y - h / 2.0) * scale, max_iter)
        if n >= max_iter
          r = 0.05
          g = 0.04
          b = 0.04
        else
          r, g, b = palette(Math.Sqrt(n) / 6.0)
        end
        row[x * 3] = (r * 255.0).to_i.to_u8
        row[x * 3 + 1] = (g * 255.0).to_i.to_u8
        row[x * 3 + 2] = (b * 255.0).to_i.to_u8
      end
      row
    end
    out = Array(UInt8).new(w * h * 3)
    for y in 0...h
      System.Array.Copy(rows[y], 0, out, y * w * 3, w * 3)
    end
    out
  end

  def self.write_ppm(path : String, px : Array(UInt8), w : Int32, h : Int32)
    File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes("P6\n#{w} #{h}\n255\n").Concat(px).ToArray)
  end
end
