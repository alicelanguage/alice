using Raylib_cs
using System.Numerics
require "./timeline"

# All geometry, choreography and camera movement are authored in Alice.
module Wonderland
  def self.v(x : Float64, y : Float64, z : Float64) : Vector3
    Vector3.new(x.to_f32, y.to_f32, z.to_f32)
  end

  def self.ink : Color
    Color.new(7, 4, 21)
  end

  def self.hue(h : Float64) : Color
    Raylib.ColorFromHSV((h % 360.0).to_f32, 0.67f32, 1.0f32)
  end

  def self.ball(x : Float64, y : Float64, z : Float64, r : Float64, c : Color)
    Raylib.DrawSphereEx(v(x, y, z), r.to_f32, 18, 28, c)
  end

  def self.oval(x : Float64, y : Float64, z : Float64, sx : Float64, sy : Float64, sz : Float64, c : Color)
    Rlgl.PushMatrix()
    Rlgl.Translatef(x.to_f32, y.to_f32, z.to_f32)
    Rlgl.Scalef(sx.to_f32, sy.to_f32, sz.to_f32)
    ball(0.0, 0.0, 0.0, 1.0, c)
    Rlgl.PopMatrix()
  end

  def self.rod(a : Vector3, b : Vector3, r : Float64, c : Color)
    Raylib.DrawCylinderEx(a, b, r.to_f32, r.to_f32, 8, c)
  end

  def self.ring(x : Float64, y : Float64, z : Float64, r : Float64, tube : Float64, c : Color)
    for i in 0...64
      a = i * Math::PI / 32.0
      b = (i + 1) * Math::PI / 32.0
      rod(v(x + Math.Cos(a) * r, y + Math.Sin(a) * r, z), v(x + Math.Cos(b) * r, y + Math.Sin(b) * r, z), tube, c)
    end
  end

  def self.mushroom(x : Float64, z : Float64, s : Float64, h : Float64, t : Float64)
    Rlgl.PushMatrix()
    Rlgl.Translatef(x.to_f32, 0.0f32, z.to_f32)
    Rlgl.Rotatef((Math.Sin(t + x) * 5.0).to_f32, 0.0f32, 0.0f32, 1.0f32)
    Rlgl.Scalef(s.to_f32, s.to_f32, s.to_f32)
    Raylib.DrawCylinderEx(v(0.0, 0.0, 0.0), v(0.0, 2.2, 0.0), 0.23f32, 0.13f32, 16, Color.new(180, 225, 245))
    Rlgl.PushMatrix()
    Rlgl.Translatef(0.0f32, 2.2f32, 0.0f32)
    Rlgl.Scalef(1.0f32, 0.42f32, 1.0f32)
    ball(0.0, 0.0, 0.0, 1.35, hue(h))
    Rlgl.PopMatrix()
    for j in 0...9
      a = j * Math::PI * 2.0 / 9.0
      ball(Math.Cos(a) * 0.83, 2.55, Math.Sin(a) * 0.83, 0.13, Color.new(255, 238, 213))
    end
    ball(0.0, 2.77, 0.0, 0.19, Color.new(255, 238, 213))
    for j in 0...18
      a = j * Math::PI / 9.0
      rod(v(0.0, 2.05, 0.0), v(Math.Cos(a) * 1.22, 2.12, Math.Sin(a) * 1.22), 0.018, hue(h + 90.0))
    end
    Rlgl.PopMatrix()
  end

  def self.card(x : Float64, y : Float64, z : Float64, angle : Float64, red : Bool)
    Rlgl.PushMatrix()
    Rlgl.Translatef(x.to_f32, y.to_f32, z.to_f32)
    Rlgl.Rotatef(angle.to_f32, 0.0f32, 1.0f32, 0.0f32)
    Rlgl.Rotatef(15.0f32, 0.0f32, 0.0f32, 1.0f32)
    Raylib.DrawCube(v(0.0, 0.0, 0.0), 0.85f32, 1.3f32, 0.045f32, Color.new(245, 226, 207))
    c = red ? Color.new(240, 35, 102) : Color.new(27, 14, 60)
    for side in [-1.0, 1.0]
      ball(-0.12, 0.12, side * 0.04, 0.17, c)
      ball(0.12, 0.12, side * 0.04, 0.17, c)
      Raylib.DrawTriangle3D(v(-0.27, 0.1, side * 0.08), v(0.0, -0.32, side * 0.08), v(0.27, 0.1, side * 0.08), c)
    end
    Rlgl.PopMatrix()
  end

  def self.clock(x : Float64, y : Float64, z : Float64, s : Float64, t : Float64)
    Rlgl.PushMatrix()
    Rlgl.Translatef(x.to_f32, y.to_f32, z.to_f32)
    Rlgl.Scalef(s.to_f32, s.to_f32, s.to_f32)
    Raylib.DrawCylinderEx(v(0.0, 0.0, -0.1), v(0.0, 0.0, 0.0), 1.0f32, 1.0f32, 64, Color.new(26, 16, 59))
    ring(0.0, 0.0, 0.02, 1.02, 0.065, Color.new(255, 193, 92))
    ring(0.0, 1.23, 0.0, 0.17, 0.05, Color.new(255, 193, 92))
    for i in 0...12
      a = i * Math::PI / 6.0
      rod(v(Math.Sin(a) * 0.78, Math.Cos(a) * 0.78, 0.04), v(Math.Sin(a) * 0.90, Math.Cos(a) * 0.90, 0.04), 0.025, Color.new(255, 220, 161))
    end
    rod(v(0.0, 0.0, 0.07), v(Math.Sin(t) * 0.75, Math.Cos(t) * 0.75, 0.07), 0.025, hue(175.0))
    rod(v(0.0, 0.0, 0.09), v(Math.Sin(t * 0.21) * 0.5, Math.Cos(t * 0.21) * 0.5, 0.09), 0.04, hue(325.0))
    ball(0.0, 0.0, 0.1, 0.07, Color.new(255, 236, 190))
    Rlgl.PopMatrix()
  end

  def self.cup(x : Float64, y : Float64, z : Float64, t : Float64, h : Float64)
    Rlgl.PushMatrix()
    Rlgl.Translatef(x.to_f32, y.to_f32, z.to_f32)
    Rlgl.Rotatef((Math.Sin(t + x) * 12.0).to_f32, 0.0f32, 0.0f32, 1.0f32)
    Raylib.DrawCylinder(v(0.0, -0.12, 0.0), 0.88f32, 0.88f32, 0.07f32, 40, hue(h))
    Raylib.DrawCylinder(v(0.0, 0.0, 0.0), 0.57f32, 0.32f32, 0.72f32, 40, Color.new(224, 211, 250))
    Raylib.DrawCylinder(v(0.0, 0.725, 0.0), 0.51f32, 0.51f32, 0.012f32, 40, Color.new(56, 18, 48))
    ring(0.65, 0.38, 0.0, 0.28, 0.07, hue(h))
    for i in 0...16
      a = t + i * 0.4
      ball(Math.Sin(a) * 0.13, 0.85 + i * 0.10, Math.Cos(a) * 0.13, 0.035 + i * 0.003, hue(h + i * 4.0))
    end
    Rlgl.PopMatrix()
  end

  def self.stars(t : Float64)
    for i in 0...220
      a = i * 2.399963
      r = 12.0 + (i % 17)
      ball(Math.Cos(a) * r, 3.0 + (i % 23) * 0.58 + Math.Sin(t * 0.4 + i) * 0.2, Math.Sin(a) * r, 0.018 + (i % 3) * 0.012, hue(i * 23.0 + t * 5.0))
    end
  end

  def self.garden(t : Float64)
    for z in -10...9
      for x in -10...11
        y = -0.25 + 0.13 * Math.Sin(x * 0.6 + t * 0.4) * Math.Cos(z * 0.5)
        c = (x + z) % 2 == 0 ? Color.new(37, 23, 66) : Color.new(95, 70, 121)
        Raylib.DrawCube(v(x * 1.3, y, z * 1.3), 1.27f32, 0.13f32, 1.27f32, c)
      end
    end
    for i in 0...28
      side = i % 2 == 0 ? -1.0 : 1.0
      x = side * (3.8 + (i % 4) * 1.3 + Math.Sin(i * 2.1) * 0.5)
      z = -10.0 + (i / 2) * 0.9 + Math.Cos(i * 1.7) * 0.45
      mushroom(x, z, 0.65 + (i % 4) * 0.23, i * 37.0 + t * 3.0, t)
    end
    # A portal nested in the garden, crowned by a floating pocket watch.
    for i in 0...5
      ring(0.0, 3.3, -6.0 - i * 0.28, 2.4 + i * 0.15, 0.055, hue(175.0 + i * 35.0 + t * 9.0))
    end
    clock(0.0, 3.3, -6.1, 1.4, t * 0.65)
    for i in 0...9
      a = t * 0.22 + i * Math::PI * 2.0 / 9.0
      card(Math.Cos(a) * 3.1, 3.8 + Math.Sin(a * 2.0) * 1.5, Math.Sin(a) * 2.0 - 4.5, -a * 57.3, i % 2 == 0)
    end
  end

  def self.tea(t : Float64)
    Raylib.DrawCylinder(v(0.0, -0.35, 0.0), 4.7f32, 4.3f32, 0.25f32, 96, Color.new(34, 17, 65))
    for i in 0...7
      a = i * Math::PI * 2.0 / 7.0 + t * 0.12
      cup(Math.Cos(a) * 3.6, 0.55 + Math.Sin(t + i) * 0.4, Math.Sin(a) * 3.6, t, i * 50.0)
    end
    # A levitating teapot, assembled from transformed solid primitives.
    ball(0.0, 2.1, 0.0, 1.1, hue(285.0 + t * 4.0))
    Raylib.DrawCylinder(v(0.0, 2.9, 0.0), 0.55f32, 0.7f32, 0.15f32, 32, Color.new(255, 186, 98))
    ball(0.0, 3.22, 0.0, 0.16, hue(175.0))
    ring(-1.15, 2.15, 0.0, 0.7, 0.15, Color.new(255, 186, 98))
    Raylib.DrawCylinderEx(v(0.7, 2.0, 0.0), v(1.7, 2.5, 0.0), 0.42f32, 0.2f32, 24, hue(310.0))
    Raylib.DrawCylinderEx(v(1.7, 2.5, 0.0), v(2.0, 3.0, 0.0), 0.2f32, 0.14f32, 24, Color.new(255, 186, 98))
    for i in 0...30
      a = i * 0.38 + t * 0.7
      ball(Math.Sin(a) * (0.3 + i * 0.025), 3.5 + i * 0.09, Math.Cos(a) * (0.3 + i * 0.025), 0.045, hue(i * 12.0 + t * 15.0))
    end
    for i in 0...12
      a = i * Math::PI / 6.0 + t * 0.13
      card(Math.Cos(a) * 6.5, 4.8 + Math.Sin(a * 3.0 + t) * 1.0, Math.Sin(a) * 6.5, -a * 57.3 + 90.0, i % 2 == 0)
    end
    clock(-4.5, 4.5, -6.0, 1.2, t)
    # The Cheshire grin: a luminous crescent and two floating eyes.
    for i in 0...24
      a = Math::PI * (1.15 + i * 0.7 / 23.0)
      ball(2.0 + Math.Cos(a) * 2.1, 6.5 + Math.Sin(a) * 1.1, -7.0, 0.13, Color.new(245, 210, 255))
    end
    oval(1.1, 6.7, -7.0, 0.37, 0.20, 0.15, hue(165.0))
    oval(2.9, 6.7, -7.0, 0.37, 0.20, 0.15, hue(165.0))
    oval(1.1, 6.7, -6.85, 0.045, 0.17, 0.035, ink)
    oval(2.9, 6.7, -6.85, 0.045, 0.17, 0.035, ink)
  end

  def self.tunnel(t : Float64)
    # The camera's ring flick knocks the spiral back by one beat.
    phase = t >= 24.7 ? t - 2.2 : t
    for i in 0...32
      z = 7.0 - i * 2.2 + ((phase - 20.0) * 2.0 % 2.2)
      r = 3.5 + Math.Sin(i * 0.5 + phase * 0.7) * 0.6
      ring(Math.Sin(i * 0.3 + phase * 0.3) * 0.6, Math.Cos(i * 0.3 + phase * 0.3) * 0.6, z, r, 0.07, hue(i * 19.0 + phase * 24.0))
      for j in 0...6
        a = j * Math::PI / 3.0 + i * 0.13 + phase * 0.16
        ball(Math.Cos(a) * r, Math.Sin(a) * r, z, 0.16, hue(i * 19.0 + 90.0))
        next_a = a + 0.13
        next_r = 3.5 + Math.Sin((i + 1) * 0.5 + phase * 0.7) * 0.6
        rod(v(Math.Cos(a) * r, Math.Sin(a) * r, z), v(Math.Cos(next_a) * next_r, Math.Sin(next_a) * next_r, z - 2.2), 0.035, hue(i * 19.0 + phase * 24.0 + 100.0))
      end
      if i % 4 == 0
        card_angle = i * 1.7 + phase * 0.3
        card(Math.Cos(card_angle) * 2.4, Math.Sin(card_angle) * 2.4, z, phase * 22.0 + i * 30.0, i % 8 == 0)
      end
    end
    clock(0.0, 0.0, -26.0, 2.6, phase * 2.0)
  end

  def self.camera(t : Float64) : Camera3D
    chapter = Film.chapter(t)
    u = Film.ease((t - chapter * 10.0) / 10.0)
    camera = Camera3D.new
    camera.Up = v(0.0, 1.0, 0.0)
    camera.FovY = 48.0f32
    camera.Projection = CameraProjection.Perspective
    if chapter == 0
      camera.Position = v(4.0 - u * 7.0, 7.0 - u * 2.0, 15.0 - u * 5.0)
      camera.Target = v(0.0, 2.1, -3.0)
    elsif chapter == 1
      a = -0.65 + u * 1.3
      camera.Position = v(Math.Sin(a) * 12.5, 6.2 - u * 1.6, Math.Cos(a) * 12.5)
      camera.Target = v(0.0, 2.7, 0.0)
    else
      depth = t - 20.0
      camera.Position = v(Math.Sin(depth * 0.4) * 0.35, Math.Cos(depth * 0.35) * 0.35, 8.0 - depth * 0.52)
      camera.Target = v(0.0, 0.0, camera.Position.Z - 33.0)
      flick = Math.Clamp((t - 24.35) / 0.55, 0.0, 1.0)
      roll = Film.ease(flick) * Math::PI * 2.0
      camera.Up = v(Math.Sin(roll), Math.Cos(roll), 0.0)
      camera.FovY = (54.0 + Math.Clamp(depth / 10.0, 0.0, 1.0) * 16.0).to_f32
    end
    camera
  end

  def self.draw(t : Float64)
    stars(t)
    case Film.chapter(t)
    when 0 then garden(t)
    when 1 then tea(t)
    else tunnel(t)
    end
  end
end
