# The original ClassicObject subclasses expressed as one typed object store and dispatch.
# Kind replaces GetType/typeof checks; generated predicates are resolved at compile time.
require "./classic"

class Entity
  getter kind : Kind
  getter g : Classic
  getter e : Pico
  property alive : Bool = true
  property collideable : Bool = true
  property solids : Bool = true
  property x : Float64
  property y : Float64
  property vx : Float64 = 0.0
  property vy : Float64 = 0.0
  property rx : Float64 = 0.0
  property ry : Float64 = 0.0
  property spr : Float64 = 0.0
  property flip_x : Bool = false
  property flip_y : Bool = false
  property hitbox : Rect = Rect.new(0.0, 0.0, 8.0, 8.0)
  property state : Int32 = 0
  property delay : Int32 = 0
  property hide_in : Int32 = 0
  property dir : Float64 = 0.0
  property dashes : Int32 = 0
  property dash_time : Int32 = 0
  property dash_effect_time : Int32 = 0
  property grace : Int32 = 0
  property jbuffer : Int32 = 0
  property hair : List(Mote) = List(Mote).new
  @p_jump : Bool = false
  @p_dash : Bool = false
  @was_ground : Bool = false
  @target_x : Float64 = 0.0
  @target_y : Float64 = 0.0
  @accel_x : Float64 = 0.0
  @accel_y : Float64 = 0.0
  @spr_off : Float64 = 0.0
  @timer : Float64 = 0.0
  @start : Float64 = 0.0
  @offset : Float64 = 0.0
  @step : Float64 = 0.5
  @last : Float64 = 0.0
  @fly : Bool = false
  @sfx_delay : Int32 = 8
  @hide_for : Int32 = 0
  @duration : Int32 = 0
  @flash : Float64 = 0.0
  @score : Int32 = 0
  @show : Bool = false
  @index : Float64 = 0.0
  @chest_particles : List(Mote) = List(Mote).new

  def initialize(@g : Classic, @e : Pico, @kind : Kind, @x : Float64, @y : Float64, tile : Int32)
    @spr = tile.to_f
  end

  define_method :can_dash? do
    @dashes > 0
  end

  define_method :facing do
    @flip_x ? -1 : 1
  end

  define_method :grounded? do
    is_solid(0.0, 1.0)
  end

  define_method :box do
    Rect.new(@x + @hitbox.x, @y + @hitbox.y, @hitbox.w, @hitbox.h)
  end

  def init
    case @kind
    when Kind::Player
      @spr = 1.0
      @dashes = @g.max_djump
      @hitbox = Rect.new(1.0, 3.0, 6.0, 5.0)
      make_hair
    when Kind::Spawn
      @spr = 3.0
      @target_x = @x
      @target_y = @y
      @y = 128.0
      @vy = -4.0
      @solids = false
      make_hair
      @e.sfx(4)
    when Kind::Balloon
      @offset = @e.rnd(1.0)
      @start = @y
      @hitbox = Rect.new(-1.0, -1.0, 10.0, 10.0)
    when Kind::Smoke
      @spr = 29.0
      @vy = -0.1
      @vx = 0.3 + @e.rnd(0.2)
      @x += -1.0 + @e.rnd(2.0)
      @y += -1.0 + @e.rnd(2.0)
      @flip_x = @e.rnd(1.0) < 0.5
      @flip_y = @e.rnd(1.0) < 0.5
      @solids = false
    when Kind::Fruit
      @spr = 26.0
      @start = @y
    when Kind::FlyFruit
      @start = @y
      @solids = false
    when Kind::LifeUp
      @vy = -0.25
      @duration = 30
      @x -= 2.0
      @y -= 4.0
      @solids = false
    when Kind::Chest
      @x -= 4.0
      @start = @x
      @timer = 20.0
    when Kind::Platform
      @x -= 4.0
      @solids = false
      @hitbox = Rect.new(0.0, 0.0, 16.0, 8.0)
      @last = @x
    when Kind::BigChest
      @hitbox = Rect.new(0.0, 0.0, 16.0, 8.0)
    when Kind::Orb
      @vy = -4.0
      @solids = false
    when Kind::Flag
      @x += 5.0
      @score = @g.got_fruit.Count
    when Kind::RoomTitle
      @delay = 5
    else
    end
  end

  def make_hair
    5.times do |i|
      h = Mote.new
      h.x = @x
      h.y = @y
      h.radius = Math.Max(1, Math.Min(2, 3 - i)).to_f
      @hair.Add(h)
    end
  end

  def collide(kind : Kind, ox : Float64, oy : Float64) : Entity?
    b = Rect.new(@x + @hitbox.x + ox, @y + @hitbox.y + oy, @hitbox.w, @hitbox.h)
    for i in 0...@g.objects.Count
      other = @g.objects[i]
      if other.alive && other.kind == kind && other != self && other.collideable && b.overlaps?(other.box)
        return other
      end
    end
    nil
  end

  def check(kind : Kind, ox : Float64, oy : Float64) : Bool
    !collide(kind, ox, oy).nil?
  end

  def is_solid(ox : Float64, oy : Float64) : Bool
    return true if oy > 0.0 && !check(Kind::Platform, ox, 0.0) && check(Kind::Platform, ox, oy)
    @g.tile_flag_at(@x + @hitbox.x + ox, @y + @hitbox.y + oy, @hitbox.w, @hitbox.h, 0) || check(Kind::FallFloor, ox, oy) || check(Kind::FakeWall, ox, oy)
  end

  def is_ice(ox : Float64, oy : Float64) : Bool
    @g.tile_flag_at(@x + @hitbox.x + ox, @y + @hitbox.y + oy, @hitbox.w, @hitbox.h, 4)
  end

  def move(ox : Float64, oy : Float64)
    @rx += ox
    amount = @e.flr(@rx + 0.5)
    @rx -= amount
    move_x(amount, 0)
    @ry += oy
    amount = @e.flr(@ry + 0.5)
    @ry -= amount
    move_y(amount)
  end

  def move_x(amount : Int32, start : Int32)
    if @solids
      step = Motion.sign(amount.to_f)
      # Preserve the inclusive loop in Classic.cs (including its extra collision step).
      for i in start..Math.Abs(amount)
        if !is_solid(step.to_f, 0.0)
          @x += step
        else
          @vx = 0.0
          @rx = 0.0
          break
        end
      end
    else
      @x += amount
    end
  end

  def move_y(amount : Int32)
    if @solids
      step = Motion.sign(amount.to_f)
      for i in 0..Math.Abs(amount)
        if !is_solid(0.0, step.to_f)
          @y += step
        else
          @vy = 0.0
          @ry = 0.0
          break
        end
      end
    else
      @y += amount
    end
  end

  def update
    case @kind
    when Kind::Player then update_player
    when Kind::Spawn then update_spawn
    when Kind::Spring then update_spring
    when Kind::Balloon then update_balloon
    when Kind::FallFloor then update_floor
    when Kind::Smoke
      @spr += 0.2
      @g.destroy(self) if @spr >= 32.0
    when Kind::Fruit
      collect_fruit
      @offset += 1.0
      @y = @start + @e.sin(@offset / 40.0) * 2.5
    when Kind::FlyFruit
      if @fly
        if @sfx_delay > 0
          @sfx_delay -= 1
          if @sfx_delay <= 0
            @g.sfx_timer = 20
            @e.sfx(14)
          end
        end
        @vy = Motion.approach(@vy, -3.5, 0.25)
        @g.destroy(self) if @y < -16.0
      else
        @fly = true if @g.has_dashed
        @step += 0.05
        @vy = @e.sin(@step) * 0.5
      end
      collect_fruit
    when Kind::LifeUp
      @duration -= 1
      @g.destroy(self) if @duration <= 0
    when Kind::FakeWall then update_fake_wall
    when Kind::Key
      was = @e.flr(@spr)
      @spr = 9.0 + (@e.sin(@g.frames / 30.0) + 0.5)
      current = @e.flr(@spr)
      @flip_x = !@flip_x if current == 10 && current != was
      if check(Kind::Player, 0.0, 0.0)
        @e.sfx(23)
        @g.sfx_timer = 20
        @g.destroy(self)
        @g.has_key = true
      end
    when Kind::Chest
      if @g.has_key
        @timer -= 1.0
        @x = @start - 1.0 + @e.rnd(3.0)
        if @timer <= 0.0
          @g.sfx_timer = 20
          @e.sfx(16)
          @g.add(Kind::Fruit, @x, @y - 4.0)
          @g.destroy(self)
        end
      end
    when Kind::Platform
      @vx = @dir * 0.65
      @x = 128.0 if @x < -16.0
      @x = -16.0 if @x > 128.0
      unless check(Kind::Player, 0.0, 0.0)
        hit = collide(Kind::Player, 0.0, -1.0)
        hit.move_x((@x - @last).to_i, 1) unless hit.nil?
      end
      @last = @x
    else
    end
  end

  def update_player
    return if @g.pause_player
    input = @e.btn(1) ? 1 : (@e.btn(0) ? -1 : 0)
    if @g.spikes_at(@x + @hitbox.x, @y + @hitbox.y, @hitbox.w, @hitbox.h, @vx, @vy) || @y > 128.0
      @g.kill_player(self)
      return
    end
    on_ground = grounded?
    on_ice = is_ice(0.0, 1.0)
    @g.add(Kind::Smoke, @x, @y + 4.0) if on_ground && !@was_ground
    jump = @e.btn(4) && !@p_jump
    @p_jump = @e.btn(4)
    if jump
      @jbuffer = 4
    elsif @jbuffer > 0
      @jbuffer -= 1
    end
    dash = @e.btn(5) && !@p_dash
    @p_dash = @e.btn(5)
    if on_ground
      @grace = 6
      if @dashes < @g.max_djump
        @g.psfx(54)
        @dashes = @g.max_djump
      end
    elsif @grace > 0
      @grace -= 1
    end
    @dash_effect_time -= 1
    if @dash_time > 0
      @g.add(Kind::Smoke, @x, @y)
      @dash_time -= 1
      @vx = Motion.approach(@vx, @target_x, @accel_x)
      @vy = Motion.approach(@vy, @target_y, @accel_y)
    else
      accel = on_ground ? (on_ice ? 0.05 : 0.6) : 0.4
      @vx = Math.Abs(@vx) > 1.0 ? Motion.approach(@vx, Motion.sign(@vx).to_f, 0.15) : Motion.approach(@vx, input.to_f, accel)
      @flip_x = @vx < 0.0 if @vx != 0.0
      maxfall = 2.0
      gravity = Math.Abs(@vy) <= 0.15 ? 0.105 : 0.21
      if input != 0 && is_solid(input.to_f, 0.0) && !is_ice(input.to_f, 0.0)
        maxfall = 0.4
        @g.add(Kind::Smoke, @x + input * 6.0, @y) if @e.rnd(10.0) < 2.0
      end
      @vy = Motion.approach(@vy, maxfall, gravity) unless on_ground
      if @jbuffer > 0
        if @grace > 0
          @g.psfx(1)
          @jbuffer = 0
          @grace = 0
          @vy = -2.0
          @g.add(Kind::Smoke, @x, @y + 4.0)
        else
          wall_dir = is_solid(-3.0, 0.0) ? -1 : (is_solid(3.0, 0.0) ? 1 : 0)
          if wall_dir != 0
            @g.psfx(2)
            @jbuffer = 0
            @vy = -2.0
            @vx = -wall_dir * 2.0
            @g.add(Kind::Smoke, @x + wall_dir * 6.0, @y) if is_ice(wall_dir * 3.0, 0.0)
          end
        end
      end
      if can_dash? && dash
        @g.add(Kind::Smoke, @x, @y)
        @dashes -= 1
        @dash_time = 4
        @g.has_dashed = true
        @dash_effect_time = 10
        dx = @e.dash_x(facing)
        dy = @e.dash_y
        full = dx != 0 && dy != 0 ? 5.0 * 0.70710678118 : 5.0
        @vx = dx * full
        @vy = dy * full
        @g.psfx(3)
        @g.freeze = 2
        @g.shake = 6
        @target_x = 2.0 * Motion.sign(@vx)
        @target_y = 2.0 * Motion.sign(@vy)
        @accel_x = @vy != 0.0 ? 1.5 * 0.70710678118 : 1.5
        @accel_y = @vx != 0.0 ? 1.5 * 0.70710678118 : 1.5
        @target_y *= 0.75 if @vy < 0.0
      elsif dash
        @g.psfx(9)
        @g.add(Kind::Smoke, @x, @y)
      end
    end
    @spr_off += 0.25
    if !on_ground
      @spr = is_solid(input.to_f, 0.0) ? 5.0 : 3.0
    elsif @e.btn(3)
      @spr = 6.0
    elsif @e.btn(2)
      @spr = 7.0
    elsif @vx == 0.0 || (!@e.btn(0) && !@e.btn(1))
      @spr = 1.0
    else
      @spr = 1.0 + @spr_off % 4.0
    end
    @g.next_room if @y < -4.0 && @g.level_index < 30
    @was_ground = on_ground
  end

  def update_spawn
    if @state == 0
      if @y < @target_y + 16.0
        @state = 1
        @delay = 3
      end
    elsif @state == 1
      @vy += 0.5
      if @vy > 0.0 && @delay > 0
        @vy = 0.0
        @delay -= 1
      end
      if @vy > 0.0 && @y > @target_y
        @y = @target_y
        @vx = 0.0
        @vy = 0.0
        @state = 2
        @delay = 5
        @g.shake = 5
        @g.add(Kind::Smoke, @x, @y + 4.0)
        @e.sfx(5)
      end
    elsif @state == 2
      @delay -= 1
      @spr = 6.0
      if @delay < 0
        @g.destroy(self)
        p = @g.add(Kind::Player, @x, @y)
        p.hair = @hair
      end
    end
  end

  def update_spring
    if @hide_for > 0
      @hide_for -= 1
      if @hide_for <= 0
        @spr = 18.0
        @delay = 0
      end
    elsif @spr == 18.0
      hit = collide(Kind::Player, 0.0, 0.0)
      if !hit.nil? && hit.vy >= 0.0
        @spr = 19.0
        hit.y = @y - 4.0
        hit.vx *= 0.2
        hit.vy = -3.0
        hit.dashes = @g.max_djump
        @delay = 10
        @g.add(Kind::Smoke, @x, @y)
        below = collide(Kind::FallFloor, 0.0, 1.0)
        @g.break_floor(below) unless below.nil?
        @g.psfx(8)
      end
    elsif @delay > 0
      @delay -= 1
      @spr = 18.0 if @delay <= 0
    end
    if @hide_in > 0
      @hide_in -= 1
      if @hide_in <= 0
        @hide_for = 60
        @spr = 0.0
      end
    end
  end

  def update_balloon
    if @spr == 22.0
      @offset += 0.01
      @y = @start + @e.sin(@offset) * 2.0
      hit = collide(Kind::Player, 0.0, 0.0)
      if !hit.nil? && hit.dashes < @g.max_djump
        @g.psfx(6)
        @g.add(Kind::Smoke, @x, @y)
        hit.dashes = @g.max_djump
        @spr = 0.0
        @timer = 60.0
      end
    elsif @timer > 0.0
      @timer -= 1.0
    else
      @g.psfx(7)
      @g.add(Kind::Smoke, @x, @y)
      @spr = 22.0
    end
  end

  def update_floor
    if @state == 0
      @g.break_floor(self) if check(Kind::Player, 0.0, -1.0) || check(Kind::Player, -1.0, 0.0) || check(Kind::Player, 1.0, 0.0)
    elsif @state == 1
      @delay -= 1
      if @delay <= 0
        @state = 2
        @delay = 60
        @collideable = false
      end
    elsif @state == 2
      @delay -= 1
      if @delay <= 0 && !check(Kind::Player, 0.0, 0.0)
        @g.psfx(7)
        @state = 0
        @collideable = true
        @g.add(Kind::Smoke, @x, @y)
      end
    end
  end

  def collect_fruit
    hit = collide(Kind::Player, 0.0, 0.0)
    unless hit.nil?
      hit.dashes = @g.max_djump
      @g.sfx_timer = 20
      @e.sfx(13)
      @g.got_fruit.Add(1 + @g.level_index)
      @g.add(Kind::LifeUp, @x, @y)
      @g.destroy(self)
    end
  end

  def update_fake_wall
    @hitbox = Rect.new(-1.0, -1.0, 18.0, 18.0)
    hit = collide(Kind::Player, 0.0, 0.0)
    if !hit.nil? && hit.dash_effect_time > 0
      hit.vx = -Motion.sign(hit.vx) * 1.5
      hit.vy = -1.5
      hit.dash_time = -1
      @g.sfx_timer = 20
      @e.sfx(16)
      @g.destroy(self)
      @g.add(Kind::Smoke, @x, @y)
      @g.add(Kind::Smoke, @x + 8.0, @y)
      @g.add(Kind::Smoke, @x, @y + 8.0)
      @g.add(Kind::Smoke, @x + 8.0, @y + 8.0)
      @g.add(Kind::Fruit, @x + 4.0, @y + 4.0)
    end
    @hitbox = Rect.new(0.0, 0.0, 16.0, 16.0)
  end

  def draw_hair(dashes : Int32)
    c = dashes == 1 ? 8 : (dashes == 2 ? 7 + @g.frames / 3 % 2 * 4 : 12)
    lx = @x + 4.0 - facing * 2.0
    ly = @y + (@e.btn(3) ? 4.0 : 3.0)
    @hair.each do |h|
      h.x += (lx - h.x) / 1.5
      h.y += (ly + 0.5 - h.y) / 1.5
      @e.circfill(h.x, h.y, h.radius, c.to_f)
      lx = h.x
      ly = h.y
    end
  end

  def draw_player(dashes : Int32)
    push = dashes == 2 ? (@g.frames / 3 % 2 == 0 ? 160 : 144) : (dashes == 0 ? 128 : 0)
    @e.spr(@spr + push, @x, @y, 1, 1, @flip_x, @flip_y)
  end

  def draw
    case @kind
    when Kind::Player
      if @x < -1.0 || @x > 121.0
        @x = Math.Clamp(@x, -1.0, 121.0)
        @vx = 0.0
      end
      draw_hair(@dashes)
      draw_player(@dashes)
    when Kind::Spawn
      draw_hair(@g.max_djump)
      draw_player(@g.max_djump)
    when Kind::Balloon
      if @spr == 22.0
        @e.spr(13.0 + @offset * 8.0 % 3.0, @x, @y + 6.0)
        @e.spr(@spr, @x, @y)
      end
    when Kind::FallFloor
      @e.spr(@state == 1 ? 23.0 + (15 - @delay) / 5 : 23.0, @x, @y) if @state != 2
    when Kind::FlyFruit
      off = 0.0
      if !@fly
        off = 1.0 + Math.Max(0, Motion.sign(@y - @start)) if @e.sin(@step) < 0.0
      else
        off = (off + 0.25) % 3.0
      end
      @e.spr(45.0 + off, @x - 6.0, @y - 2.0, 1, 1, true, false)
      @e.spr(@spr, @x, @y)
      @e.spr(45.0 + off, @x + 6.0, @y - 2.0)
    when Kind::LifeUp
      @flash += 0.5
      @e.print("1000", @x - 2.0, @y, 7.0 + @flash % 2.0)
    when Kind::FakeWall
      @e.spr(64.0, @x, @y)
      @e.spr(65.0, @x + 8.0, @y)
      @e.spr(80.0, @x, @y + 8.0)
      @e.spr(81.0, @x + 8.0, @y + 8.0)
    when Kind::Platform
      @e.spr(11.0, @x, @y - 1.0)
      @e.spr(12.0, @x + 8.0, @y - 1.0)
    when Kind::Message then draw_message
    when Kind::BigChest then draw_big_chest
    when Kind::Orb then draw_orb
    when Kind::Flag
      @spr = 118.0 + (@g.frames / 5.0) % 3.0
      @e.spr(@spr, @x, @y)
      if @show
        @e.rectfill(32.0, 2.0, 96.0, 31.0, 0.0)
        @e.spr(26.0, 55.0, 6.0)
        @e.print("x#{@score}", 64.0, 9.0, 7.0)
        @g.draw_time(49.0, 16.0)
        @e.print("deaths:#{@g.deaths}", 48.0, 24.0, 7.0)
      elsif check(Kind::Player, 0.0, 0.0)
        @e.sfx(55)
        @g.sfx_timer = 30
        @show = true
        @g.completed = true
      end
    when Kind::RoomTitle
      @delay -= 1
      if @delay < -30
        @g.destroy(self)
      elsif @delay < 0
        @e.rectfill(24.0, 58.0, 104.0, 70.0, 0.0)
        if @g.room_x == 3 && @g.room_y == 1
          @e.print("old site", 48.0, 62.0, 7.0)
        elsif @g.level_index == 30
          @e.print("summit", 52.0, 62.0, 7.0)
        else
          level = (1 + @g.level_index) * 100
          @e.print("#{level}m", level < 1000 ? 54.0 : 52.0, 62.0, 7.0)
        end
        @g.draw_time(4.0, 4.0)
      end
    else
      @e.spr(@spr, @x, @y, 1, 1, @flip_x, @flip_y) if @spr > 0.0
    end
  end

  def draw_message
    text = "-- celeste mountain --#this memorial to those# perished on the climb"
    if check(Kind::Player, 4.0, 0.0)
      if @index < text.Length
        @index += 0.5
        if @index >= @last + 1.0
          @last += 1.0
          @e.sfx(35)
        end
      end
      ox = 8.0
      oy = 96.0
      i = 0
      while i < @index && i < text.Length
        if text[i] != '#'
          @e.rectfill(ox - 2.0, oy - 2.0, ox + 7.0, oy + 6.0, 7.0)
          @e.print(text[i].ToString, ox, oy, 0.0)
          ox += 5.0
        else
          ox = 8.0
          oy += 7.0
        end
        i += 1
      end
    else
      @index = 0.0
      @last = 0.0
    end
  end

  def draw_big_chest
    if @state == 0
      hit = collide(Kind::Player, 0.0, 8.0)
      if !hit.nil? && hit.grounded?
        @e.music(-1, 500, 7)
        @e.sfx(37)
        @g.pause_player = true
        hit.vx = 0.0
        hit.vy = 0.0
        @state = 1
        @g.add(Kind::Smoke, @x, @y)
        @g.add(Kind::Smoke, @x + 8.0, @y)
        @timer = 60.0
        @chest_particles.Clear()
      end
      @e.spr(96.0, @x, @y)
      @e.spr(97.0, @x + 8.0, @y)
    elsif @state == 1
      @timer -= 1.0
      @g.shake = 5
      @g.flash_bg = true
      if @timer <= 45.0 && @chest_particles.Count < 50
        p = Mote.new
        p.x = 1.0 + @e.rnd(14.0)
        p.width = 32.0 + @e.rnd(32.0)
        p.speed = 8.0 + @e.rnd(8.0)
        @chest_particles.Add(p)
      end
      if @timer < 0.0
        @state = 2
        @chest_particles.Clear()
        @g.flash_bg = false
        @g.new_bg = true
        @g.add(Kind::Orb, @x + 4.0, @y + 4.0)
        @g.pause_player = false
      end
      @chest_particles.each do |p|
        p.y += p.speed
        @e.rectfill(@x + p.x, @y + 8.0 - p.y, @x + p.x + 1.0, Math.Min(@y + 8.0 - p.y + p.width, @y + 8.0), 7.0)
      end
    end
    @e.spr(112.0, @x, @y + 8.0)
    @e.spr(113.0, @x + 8.0, @y + 8.0)
  end

  def draw_orb
    @vy = Motion.approach(@vy, 0.0, 0.5)
    hit = collide(Kind::Player, 0.0, 0.0)
    if @vy == 0.0 && !hit.nil?
      @g.music_timer = 45
      @e.sfx(51)
      @g.freeze = 10
      @g.shake = 10
      @g.destroy(self)
      @g.max_djump = 2
      hit.dashes = 2
    end
    @e.spr(102.0, @x, @y)
    off = @g.frames / 30.0
    for i in 0..7
      @e.circfill(@x + 4.0 + @e.cos(off + i / 8.0) * 8.0, @y + 4.0 + @e.sin(off + i / 8.0) * 8.0, 1.0, 7.0)
    end
  end
end
