# Celeste Classic, rewritten in Alice from Noel Berry's MIT-licensed PICO-8 port.
# https://github.com/NoelFB/Celeste/blob/master/Source/PICO-8/Classic.cs
require "./pico"
require "./objects"

enum Kind
  Player
  Spawn
  Spring
  Balloon
  FallFloor
  Smoke
  Fruit
  FlyFruit
  LifeUp
  FakeWall
  Key
  Chest
  Platform
  Message
  BigChest
  Orb
  Flag
  RoomTitle
end

record struct Rect(x : Float64, y : Float64, w : Float64, h : Float64)
  def overlaps?(b : Rect) : Bool
    x + w > b.x && y + h > b.y && x < b.x + b.w && y < b.y + b.h
  end
end

# These attributes generate typed column stores; no reflection or handwritten storage layer.
@[SoA]
record struct Snow(x : Float64, y : Float64, speed : Float64, phase : Float64, radius : Float64, color : Int32)
@[SoA]
record struct Cloud(x : Float64, y : Float64, speed : Float64, width : Float64)

class Mote
  property x : Float64 = 0.0
  property y : Float64 = 0.0
  property speed : Float64 = 0.0
  property width : Float64 = 0.0
  property phase : Float64 = 0.0
  property radius : Float64 = 0.0
  property color : Int32 = 7
  property life : Int32 = 10
  property vx : Float64 = 0.0
  property vy : Float64 = 0.0
end

module Motion
  def self.approach(value : Float64, target : Float64, amount : Float64) : Float64
    value > target ? Math.Max(value - amount, target) : Math.Min(value + amount, target)
  end

  def self.sign(value : Float64) : Int32
    value > 0.0 ? 1 : (value < 0.0 ? -1 : 0)
  end
end

class Classic
  getter e : Pico
  getter objects : List(Entity) = List(Entity).new
  getter got_fruit : HashSet(Int32) = HashSet(Int32).new
  property room_x : Int32 = 7
  property room_y : Int32 = 3
  property freeze : Int32 = 0
  property shake : Int32 = 0
  property has_dashed : Bool = false
  property has_key : Bool = false
  property pause_player : Bool = false
  property flash_bg : Bool = false
  property new_bg : Bool = false
  property music_timer : Int32 = 0
  property sfx_timer : Int32 = 0
  property frames : Int32 = 0
  property seconds : Int32 = 0
  property minutes : Int32 = 0
  property deaths : Int32 = 0
  property max_djump : Int32 = 1
  property completed : Bool = false
  property will_restart : Bool = false
  property delay_restart : Int32 = 0
  @start_game : Bool = false
  @start_game_flash : Int32 = 0
  getter clouds : SoA(Cloud) = SoA(Cloud).new
  getter particles : SoA(Snow) = SoA(Snow).new
  @dead_particles : List(Mote) = List(Mote).new

  def initialize(@e : Pico)
    17.times do |i|
      @clouds.add(Cloud.new(@e.rnd(128.0), @e.rnd(128.0), 1.0 + @e.rnd(4.0), 32.0 + @e.rnd(32.0)))
    end
    33.times do |i|
      x = @e.rnd(128.0)
      y = @e.rnd(128.0)
      radius = @e.flr(@e.rnd(5.0) / 4.0).to_f
      speed = 0.25 + @e.rnd(5.0)
      phase = @e.rnd(1.0)
      color = 6 + @e.flr(0.5 + @e.rnd(1.0))
      @particles.add(Snow.new(x, y, speed, phase, radius, color))
    end
    title_screen
  end

  def title_screen
    @got_fruit.Clear()
    @frames = 0
    @deaths = 0
    @max_djump = 1
    @completed = false
    @start_game = false
    @start_game_flash = 0
    @e.music(40)
    load_room(7, 3)
  end

  def begin_game
    @frames = 0
    @seconds = 0
    @minutes = 0
    @music_timer = 0
    @start_game = false
    @e.music(0)
    load_room(0, 0)
  end

  def level_index : Int32
    @room_x % 8 + @room_y * 8
  end

  define_method :is_title do
    level_index == 31
  end

  def add(kind : Kind, x : Float64, y : Float64, tile : Int32 = 0) : Entity
    obj = Entity.new(self, @e, kind, x, y, tile)
    @objects.Add(obj)
    obj.init
    obj
  end

  def destroy(obj : Entity)
    obj.alive = false
  end

  def kill_player(obj : Entity)
    return unless obj.alive
    @sfx_timer = 12
    @e.sfx(0)
    @deaths += 1
    @shake = 10
    destroy(obj)
    @dead_particles.Clear()
    for dir in 0..7
      p = Mote.new
      angle = dir / 8.0
      p.x = obj.x + 4.0
      p.y = obj.y + 4.0
      p.vx = @e.cos(angle) * 3.0
      p.vy = @e.sin(angle + 0.5) * 3.0
      @dead_particles.Add(p)
    end
    @will_restart = true
    @delay_restart = 15
  end

  def psfx(id : Int32)
    @e.sfx(id) if @sfx_timer <= 0
  end

  def break_floor(obj : Entity)
    if obj.state == 0
      psfx(15)
      obj.state = 1
      obj.delay = 15
      add(Kind::Smoke, obj.x, obj.y)
      spring = obj.collide(Kind::Spring, 0.0, -1.0)
      spring.hide_in = 15 unless spring.nil?
    end
  end

  def next_room
    if @room_x == 2 && @room_y == 1
      @e.music(30, 500, 7)
    elsif @room_x == 3 && @room_y == 1
      @e.music(20, 500, 7)
    elsif (@room_x == 4 && @room_y == 2) || (@room_x == 5 && @room_y == 3)
      @e.music(30, 500, 7)
    end
    if @room_x == 7
      load_room(0, @room_y + 1)
    else
      load_room(@room_x + 1, @room_y)
    end
  end

  def load_room(rx : Int32, ry : Int32)
    @has_dashed = false
    @has_key = false
    @will_restart = false
    @room_x = rx
    @room_y = ry
    @objects.each { |obj| obj.alive = false }
    @e.each_tile(rx * 16, ry * 16, 16, 16) do |tx, ty, tile|
      x = (tx * 8).to_f
      y = (ty * 8).to_f
      case tile
      when 11, 12
        obj = add(Kind::Platform, x, y)
        obj.dir = tile == 11 ? -1.0 : 1.0
      when 1 then add(Kind::Spawn, x, y, tile)
      when 18 then add(Kind::Spring, x, y, tile)
      when 22 then add(Kind::Balloon, x, y, tile)
      when 23 then add(Kind::FallFloor, x, y, tile)
      when 86 then add(Kind::Message, x, y, tile)
      when 96 then add(Kind::BigChest, x, y, tile)
      when 118 then add(Kind::Flag, x, y, tile)
      else
        unless @got_fruit.Contains(1 + level_index)
          case tile
          when 26 then add(Kind::Fruit, x, y, tile)
          when 28 then add(Kind::FlyFruit, x, y, tile)
          when 64 then add(Kind::FakeWall, x, y, tile)
          when 8 then add(Kind::Key, x, y, tile)
          when 20 then add(Kind::Chest, x, y, tile)
          end
        end
      end
    end
    add(Kind::RoomTitle, 0.0, 0.0) unless is_title
  end

  def update
    @frames = (@frames + 1) % 30
    if @frames == 0 && level_index < 30
      @seconds = (@seconds + 1) % 60
      @minutes += 1 if @seconds == 0
    end
    if @music_timer > 0
      @music_timer -= 1
      @e.music(10) if @music_timer <= 0
    end
    @sfx_timer -= 1 if @sfx_timer > 0
    if @freeze > 0
      @freeze -= 1
      return
    end
    if @shake > 0
      @shake -= 1
      @e.camera
      @e.camera(-2.0 + @e.rnd(5.0), -2.0 + @e.rnd(5.0)) if @shake > 0
    end
    if @will_restart && @delay_restart > 0
      @delay_restart -= 1
      load_room(@room_x, @room_y) if @delay_restart <= 0
    end
    # Capture the length: entities spawned in update start moving on the next tick.
    length = @objects.Count
    for i in 0...length
      obj = @objects[i]
      if obj.alive
        obj.move(obj.vx, obj.vy)
        obj.update
      end
    end
    i = @objects.Count - 1
    while i >= 0
      @objects.RemoveAt(i) unless @objects[i].alive
      i -= 1
    end
    if is_title
      if !@start_game && (@e.btn(4) || @e.btn(5))
        @e.music(-1, 0, 0)
        @start_game_flash = 50
        @start_game = true
        @e.sfx(38)
      end
      if @start_game
        @start_game_flash -= 1
        begin_game if @start_game_flash <= -30
      end
    end
  end

  def draw
    @e.pal
    if @start_game
      c = 10
      if @start_game_flash > 10
        c = 7 if @frames % 10 < 5
      elsif @start_game_flash > 5
        c = 2
      elsif @start_game_flash > 0
        c = 1
      else
        c = 0
      end
      [6, 12, 13, 5, 1, 7].each { |p| @e.pal(p, c) } if c < 10
    end
    bg = @flash_bg ? @frames / 5 : (@new_bg ? 2 : 0)
    @e.rectfill(0.0, 0.0, 128.0, 128.0, bg.to_f)
    unless is_title
      @clouds.each do |c|
        c.x += c.speed
        @e.rectfill(c.x, c.y, c.x + c.width, c.y + 4.0 + (1.0 - c.width / 64.0) * 12.0, @new_bg ? 14.0 : 1.0)
        if c.x > 128.0
          c.x = -c.width
          c.y = @e.rnd(120.0)
        end
      end
    end
    @e.map(@room_x * 16, @room_y * 16, 0, 0, 16, 16, 2)
    i = 0
    while i < @objects.Count
      obj = @objects[i]
      obj.draw if obj.alive && (obj.kind == Kind::Platform || obj.kind == Kind::BigChest)
      i += 1
    end
    @e.map(@room_x * 16, @room_y * 16, is_title ? -4 : 0, 0, 16, 16, 1)
    i = 0
    while i < @objects.Count
      obj = @objects[i]
      obj.draw if obj.alive && obj.kind != Kind::Platform && obj.kind != Kind::BigChest
      i += 1
    end
    @e.map(@room_x * 16, @room_y * 16, 0, 0, 16, 16, 3)
    @particles.each do |p|
      p.x += p.speed
      p.y += @e.sin(p.phase)
      p.phase += Math.Min(0.05, p.speed / 32.0)
      @e.rectfill(p.x, p.y, p.x + p.radius, p.y + p.radius, p.color.to_f)
      if p.x > 132.0
        p.x = -4.0
        p.y = @e.rnd(128.0)
      end
    end
    i = @dead_particles.Count - 1
    while i >= 0
      p = @dead_particles[i]
      p.x += p.vx
      p.y += p.vy
      p.life -= 1
      @dead_particles.RemoveAt(i) if p.life <= 0
      @e.rectfill(p.x - p.life / 5, p.y - p.life / 5, p.x + p.life / 5, p.y + p.life / 5, (14 + p.life % 2).to_f)
      i -= 1
    end
    @e.rectfill(-5.0, -5.0, -1.0, 133.0, 0.0)
    @e.rectfill(-5.0, -5.0, 133.0, -1.0, 0.0)
    @e.rectfill(-5.0, 128.0, 133.0, 133.0, 0.0)
    @e.rectfill(128.0, -5.0, 133.0, 133.0, 0.0)
    if is_title
      @e.print("press z or x", 40.0, 92.0, 7.0)
      @e.print("maddy thorson", 38.0, 102.0, 5.0)
      @e.print("noel berry", 44.0, 109.0, 5.0)
      @e.print("rewritten in alice", 28.0, 120.0, 6.0)
    end
    if level_index == 30
      @objects.each do |p|
        if p.alive && p.kind == Kind::Player
          diff = Math.Min(24.0, 40.0 - Math.Abs(p.x + 4.0 - 64.0))
          @e.rectfill(0.0, 0.0, diff, 128.0, 0.0)
          @e.rectfill(128.0 - diff, 0.0, 128.0, 128.0, 0.0)
        end
      end
    end
  end

  def draw_time(x : Float64, y : Float64)
    h = @e.flr(@minutes / 60.0)
    m = @minutes % 60
    s = @seconds
    text = "#{h < 10 ? "0" : ""}#{h}:#{m < 10 ? "0" : ""}#{m}:#{s < 10 ? "0" : ""}#{s}"
    @e.rectfill(x, y, x + 32.0, y + 6.0, 0.0)
    @e.print(text, x + 1.0, y + 1.0, 7.0)
  end

  def tile_at(x : Int32, y : Int32) : Int32
    @e.mget(@room_x * 16 + x, @room_y * 16 + y)
  end

  def tile_flag_at(x : Float64, y : Float64, w : Float64, h : Float64, flag : Int32) : Bool
    for i in Math.Max(0, @e.flr(x / 8.0))..Math.Min(15, @e.flr((x + w - 1.0) / 8.0))
      for j in Math.Max(0, @e.flr(y / 8.0))..Math.Min(15, @e.flr((y + h - 1.0) / 8.0))
        return true if @e.fget(tile_at(i, j), flag)
      end
    end
    false
  end

  def spikes_at(x : Float64, y : Float64, w : Float64, h : Float64, vx : Float64, vy : Float64) : Bool
    for i in Math.Max(0, @e.flr(x / 8.0))..Math.Min(15, @e.flr((x + w - 1.0) / 8.0))
      for j in Math.Max(0, @e.flr(y / 8.0))..Math.Min(15, @e.flr((y + h - 1.0) / 8.0))
        tile = tile_at(i, j)
        return true if tile == 17 && ((y + h - 1.0) % 8.0 >= 6.0 || y + h == j * 8 + 8) && vy >= 0.0
        return true if tile == 27 && y % 8.0 <= 2.0 && vy <= 0.0
        return true if tile == 43 && x % 8.0 <= 2.0 && vx <= 0.0
        return true if tile == 59 && ((x + w - 1.0) % 8.0 >= 6.0 || x + w == i * 8 + 8) && vx >= 0.0
      end
    end
    false
  end
end
