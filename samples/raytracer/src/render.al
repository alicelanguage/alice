require "./scene"

class Camera
  getter eye : Vec
  getter target : Vec

  def initialize(@eye : Vec, @target : Vec)
  end

  # Ray through pixel (px, py) of a w×h image, 50° vertical field of view.
  def ray(px : Float64, py : Float64, w : Int32, h : Int32) : Ray
    forward = (@target - @eye).unit
    right = Vec.new(forward.z, 0.0, -forward.x).unit
    up = Vec.new(right.y * forward.z - right.z * forward.y, right.z * forward.x - right.x * forward.z, right.x * forward.y - right.y * forward.x)
    scale = Math.Tan(25.0 * Math::PI / 180.0)
    aspect = w.to_f / h
    u = (2.0 * (px + 0.5) / w - 1.0) * aspect * scale
    v = (1.0 - 2.0 * (py + 0.5) / h) * scale
    Ray.new(@eye, (forward + right * u + up * v).unit)
  end
end

module Render
  # Renders rows in parallel (Par.Map over row numbers: each row is its own array, so no shared writes),
  # 2×2 supersampling, gamma 2.2; returns RGB bytes.
  def self.image(scene : Scene, cam : Camera, w : Int32, h : Int32) : Array(UInt8)
    rows = Par.Map((0...h).to_a.ToArray) do |y|
      row = Array(UInt8).new(w * 3)
      for x in 0...w
        c = Vec.new(0.0, 0.0, 0.0)
        for s in 0...4
          c = c + scene.trace(cam.ray(x + (s % 2) * 0.5 - 0.25, y + (s / 2) * 0.5 - 0.25, w, h), 0)
        end
        c = c * 0.25
        row[x * 3] = byte(c.x)
        row[x * 3 + 1] = byte(c.y)
        row[x * 3 + 2] = byte(c.z)
      end
      row
    end
    out = Array(UInt8).new(w * h * 3)
    for y in 0...h
      System.Array.Copy(rows[y], 0, out, y * w * 3, w * 3)
    end
    out
  end

  def self.byte(v : Float64) : UInt8
    Math.Clamp(Math.Pow(Math.Clamp(v, 0.0, 1.0), 1.0 / 2.2) * 255.0 + 0.5, 0.0, 255.0).to_i.to_u8
  end

  def self.write_ppm(path : String, px : Array(UInt8), w : Int32, h : Int32)
    header = System.Text.Encoding.ASCII.GetBytes("P6\n#{w} #{h}\n255\n")
    File.WriteAllBytes(path, header.Concat(px).ToArray)
  end
end
