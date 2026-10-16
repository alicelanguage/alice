require "../src/pico"
require "../src/classic"
require "../src/objects"
require "../src/persistence"

def test_original_cartridge_rooms_and_flags
  e = Pico.new
  assert_equal 8192, e.tiles.Length
  assert_equal 1, e.mget(1, 12)
  assert e.fget(35, 0)
  assert e.fget(66, 4)
  assert !e.fget(1, 0)
end

def test_title_and_begin_game
  g = Classic.new(Pico.new)
  assert g.is_title
  g.begin_game
  assert_equal 0, g.level_index
  assert_equal 1, g.max_djump
  assert g.objects.Count > 0
end

def test_pico_palette_camera_and_inclusive_pixels
  e = Pico.new
  e.rectfill(0.0, 0.0, 0.0, 0.0, 8.0)
  assert_equal 8, e.pixels[0]
  e.pal(8, 12)
  e.rectfill(1.0, 0.0, 1.0, 0.0, 8.0)
  assert_equal 12, e.pixels[1]
  e.camera(2.0, 0.0)
  e.rectfill(2.0, 0.0, 2.0, 0.0, 7.0)
  assert_equal 7, e.pixels[0]
end

def test_pico_trigonometry_uses_turns_and_inverted_sine
  e = Pico.new
  assert Math.Abs(e.sin(0.25) + 1.0) < 0.00001
  assert Math.Abs(e.cos(0.5) + 1.0) < 0.00001
end

def empty_game : Classic
  e = Pico.new(17)
  g = Classic.new(e)
  g.begin_game
  g.objects.Clear()
  for i in 0...e.tiles.Length
    e.tiles[i] = 0
  end
  g
end

def floor(g : Classic, row : Int32)
  for x in 0...16
    g.e.tiles[x + row * 128] = 35
  end
end

def alive_count(g : Classic, kind : Kind) : Int32
  count = 0
  g.objects.each { |o| count += 1 if o.alive && o.kind == kind }
  count
end

def test_original_rooms_load_every_entity_kind
  g = Classic.new(Pico.new)
  kinds = HashSet(Kind).new
  for room in 0...32
    g.load_room(room % 8, room / 8)
    assert_equal room, g.level_index
    g.objects.each { |o| kinds.Add(o.kind) if o.alive }
  end
  for kind in [Kind::Spawn, Kind::Spring, Kind::Balloon, Kind::FallFloor, Kind::Fruit, Kind::FlyFruit, Kind::FakeWall, Kind::Key, Kind::Chest, Kind::Platform, Kind::Message, Kind::BigChest, Kind::Flag]
    assert kinds.Contains(kind), "missing original map object #{kind}"
  end
end

def test_generated_soa_stores_keep_snow_and_cloud_updates
  g = Classic.new(Pico.new)
  g.begin_game
  assert_equal 33, g.particles.size
  assert_equal 17, g.clouds.size
  x = g.particles[0].x
  speed = g.particles[0].speed
  g.draw
  expected = x + speed > 132.0 ? -4.0 : x + speed
  assert Math.Abs(expected - g.particles[0].x) < 0.000001
end

def test_generated_player_methods_and_box_record
  g = empty_game
  p = g.add(Kind::Player, 32.0, 64.0)
  assert p.can_dash?
  p.dashes = 0
  assert !p.can_dash?
  assert_equal 1, p.facing
  p.flip_x = true
  assert_equal -1, p.facing
  assert_equal Rect.new(33.0, 67.0, 6.0, 5.0), p.box
  assert !p.box.overlaps?(Rect.new(39.0, 67.0, 6.0, 5.0))
end

def test_ground_jump_grace_and_held_input
  g = empty_game
  floor(g, 10)
  p = g.add(Kind::Player, 32.0, 72.0)
  g.update
  assert p.grounded?
  assert_equal 6, p.grace
  g.e.buttons[4] = true
  g.update
  assert_equal -2.0, p.vy
  assert_equal 0, p.jbuffer
  p.update
  assert_equal 0, p.jbuffer
end

def test_coyote_jump_after_leaving_floor
  g = empty_game
  floor(g, 10)
  p = g.add(Kind::Player, 32.0, 72.0)
  g.update
  for x in 0...16
    g.e.tiles[x + 10 * 128] = 0
  end
  g.e.buttons[4] = true
  g.update
  assert_equal -2.0, p.vy
  assert_equal 0, p.grace
end

def test_jump_buffer_on_landing
  g = empty_game
  floor(g, 10)
  p = g.add(Kind::Player, 32.0, 68.0)
  g.e.buttons[4] = true
  p.update
  assert_equal 4, p.jbuffer
  p.y = 72.0
  g.e.buttons[4] = false
  p.update
  assert_equal -2.0, p.vy
  assert_equal 0, p.jbuffer
end

def test_wall_slide_and_wall_jump
  g = empty_game
  for y in 0...16
    g.e.tiles[4 + y * 128] = 35
  end
  p = g.add(Kind::Player, 25.0, 64.0)
  p.vy = 0.4
  g.e.buttons[1] = true
  p.update
  assert p.vy <= 0.4
  g.e.buttons[4] = true
  p.update
  assert_equal -2.0, p.vy
  assert_equal -2.0, p.vx
end

def test_all_eight_dash_directions_and_default_facing
  for dx in -1..1
    for dy in -1..1
      g = empty_game
      p = g.add(Kind::Player, 48.0, 64.0)
      g.e.buttons[0] = dx < 0
      g.e.buttons[1] = dx > 0
      g.e.buttons[2] = dy < 0
      g.e.buttons[3] = dy > 0
      g.e.buttons[5] = true
      p.update
      assert_equal 0, p.dashes
      assert_equal 4, p.dash_time
      assert_equal 2, g.freeze
      assert g.has_dashed
      assert Math.Abs(Math.Sqrt(p.vx * p.vx + p.vy * p.vy) - 5.0) < 0.00001
      assert Motion.sign(p.vy) == dy
      assert Motion.sign(p.vx) == (dx == 0 && dy == 0 ? 1 : dx)
    end
  end
end

def test_dash_is_edge_triggered_and_landing_refills
  g = empty_game
  p = g.add(Kind::Player, 48.0, 64.0)
  g.e.buttons[5] = true
  p.update
  8.times { |i| p.update }
  assert_equal 0, p.dashes
  assert_equal 0, p.dash_time
  floor(g, 10)
  p.y = 72.0
  p.update
  assert_equal 1, p.dashes
  assert_equal 0, p.dash_time
end

def test_fast_dash_cannot_tunnel_through_tiles
  g = empty_game
  for y in 0...16
    g.e.tiles[8 + y * 128] = 35
  end
  p = g.add(Kind::Player, 48.0, 64.0)
  p.move(30.0, 0.0)
  assert p.box.x + p.box.w <= 64.0
  assert_equal 0.0, p.vx
end

def test_directional_spikes_and_pit_death
  g = empty_game
  g.e.tiles[4 + 10 * 128] = 17
  assert g.spikes_at(32.0, 85.0, 6.0, 2.0, 0.0, 1.0)
  assert !g.spikes_at(32.0, 80.0, 6.0, 2.0, 0.0, 1.0)
  assert !g.spikes_at(32.0, 85.0, 6.0, 2.0, 0.0, -1.0)
  p = g.add(Kind::Player, 32.0, 130.0)
  p.update
  assert_equal 1, g.deaths
  assert !p.alive
  assert_equal 15, g.delay_restart
end

def test_death_respawns_in_original_room
  g = Classic.new(Pico.new)
  g.begin_game
  80.times { |i| g.update; g.draw }
  assert_equal 1, alive_count(g, Kind::Player)
  g.objects.each { |p| g.kill_player(p) if p.alive && p.kind == Kind::Player }
  assert_equal 1, g.deaths
  100.times { |i| g.update; g.draw }
  assert_equal 0, g.level_index
  assert_equal 1, alive_count(g, Kind::Player)
end

def test_spring_and_balloon_refill_dashes
  g = empty_game
  p = g.add(Kind::Player, 48.0, 64.0)
  p.dashes = 0
  spring = g.add(Kind::Spring, 48.0, 64.0, 18)
  spring.update
  assert_equal -3.0, p.vy
  assert_equal 1, p.dashes
  p.dashes = 0
  balloon = g.add(Kind::Balloon, p.x, p.y, 22)
  balloon.update
  assert_equal 1, p.dashes
  assert_equal 0.0, balloon.spr
  p.x = 16.0
  61.times { |i| balloon.update }
  assert_equal 22.0, balloon.spr
end

def test_crumbling_floor_hides_then_waits_for_player_to_leave
  g = empty_game
  p = g.add(Kind::Player, 48.0, 72.0)
  f = g.add(Kind::FallFloor, 48.0, 80.0, 23)
  f.update
  assert_equal 1, f.state
  15.times { |i| f.update }
  assert !f.collideable
  assert_equal 2, f.state
  p.y = 80.0
  61.times { |i| f.update }
  assert_equal 2, f.state
  p.x = 16.0
  f.update
  assert_equal 0, f.state
  assert f.collideable
end

def test_fruit_survives_room_restart_and_refills_player
  g = empty_game
  p = g.add(Kind::Player, 48.0, 64.0)
  p.dashes = 0
  g.e.tiles[6 + 8 * 128] = 26
  f = g.add(Kind::Fruit, 48.0, 64.0, 26)
  f.update
  assert !f.alive
  assert_equal 1, p.dashes
  assert g.got_fruit.Contains(1)
  g.load_room(0, 0)
  assert_equal 0, alive_count(g, Kind::Fruit)
  assert_equal 1, g.got_fruit.Count
  g.title_screen
  assert_equal 0, g.got_fruit.Count
end

def test_flying_fruit_flees_after_dash
  g = empty_game
  f = g.add(Kind::FlyFruit, 80.0, 64.0, 28)
  g.has_dashed = true
  f.update
  20.times { |i| f.update }
  assert f.vy < -3.0
end

def test_key_opens_chest_into_fruit
  g = empty_game
  p = g.add(Kind::Player, 48.0, 64.0)
  key = g.add(Kind::Key, 48.0, 64.0, 8)
  chest = g.add(Kind::Chest, 80.0, 64.0, 20)
  key.update
  assert g.has_key
  assert !key.alive
  20.times { |i| chest.update }
  assert !chest.alive
  assert_equal 1, alive_count(g, Kind::Fruit)
end

def test_dash_breaks_fake_wall_into_fruit
  g = empty_game
  p = g.add(Kind::Player, 48.0, 64.0)
  p.vx = 5.0
  p.dash_effect_time = 10
  wall = g.add(Kind::FakeWall, 48.0, 64.0, 64)
  wall.update
  assert !wall.alive
  assert_equal -1.5, p.vx
  assert_equal -1.5, p.vy
  assert_equal 1, alive_count(g, Kind::Fruit)
end

def test_moving_platform_is_one_way_and_carries_player
  g = empty_game
  p = g.add(Kind::Player, 32.0, 72.0)
  platform = g.add(Kind::Platform, 32.0, 80.0)
  platform.dir = 1.0
  assert p.is_solid(0.0, 1.0)
  assert !p.is_solid(0.0, -1.0)
  platform.x += 2.0
  platform.update
  assert_equal 34.0, p.x
end

def test_big_chest_sequence_unlocks_double_dash_orb
  g = empty_game
  floor(g, 10)
  p = g.add(Kind::Player, 32.0, 72.0)
  chest = g.add(Kind::BigChest, 32.0, 64.0, 96)
  chest.draw
  assert g.pause_player
  61.times { |i| chest.draw }
  assert !g.pause_player
  assert g.new_bg
  assert_equal 1, alive_count(g, Kind::Orb)
  g.objects.each do |o|
    if o.alive && o.kind == Kind::Orb
      o.vy = 0.0
      p.x = o.x
      p.y = o.y
      o.draw
    end
  end
  assert_equal 2, g.max_djump
  assert_equal 2, p.dashes
end

def test_room_exit_and_summit_flag
  g = empty_game
  p = g.add(Kind::Player, 32.0, -8.0)
  p.update
  assert_equal 1, g.level_index
  g.load_room(6, 3)
  flag = g.add(Kind::Flag, 40.0, 64.0, 118)
  hero = g.add(Kind::Player, flag.x, flag.y)
  flag.draw
  assert g.completed
end

def test_json_checkpoint_roundtrip
  g = empty_game
  g.load_room(4, 2)
  g.deaths = 9
  g.minutes = 2
  g.seconds = 17
  g.max_djump = 2
  g.new_bg = true
  g.got_fruit.Add(1)
  g.got_fruit.Add(5)
  text = Persistence.json(Persistence.capture(g))
  save = Persistence.progress(text)
  other = Classic.new(Pico.new)
  Persistence.restore(other, save)
  assert_equal 20, other.level_index
  assert_equal 9, other.deaths
  assert_equal 2, other.max_djump
  assert_equal 17, other.seconds
  assert other.got_fruit.Contains(5)
  assert_equal text, Persistence.json(Persistence.capture(other))
end

def test_json_rejects_invalid_checkpoint_before_mutating_game
  invalid = Progress.new(99, 50, 0, 0, 0, 1, false, [1], false)
  rejected = false
  begin
    Persistence.progress(Persistence.json(invalid))
  rescue ex : Exception
    rejected = true
  end
  assert rejected
end

def test_serialized_replay_reproduces_simulation_and_pixels
  frames = List(InputFrame).new
  for i in 0...240
    frames.Add(InputFrame.new(i % 60 > 40, i % 60 < 30, i % 80 < 20, false, i % 13 < 4, i % 23 == 0))
  end
  replay = Persistence.replay(Persistence.json(Replay.new(1, 17, frames.ToArray)))
  a = Classic.new(Pico.new(replay.seed))
  b = Classic.new(Pico.new(replay.seed))
  a.begin_game
  b.begin_game
  for i in 0...frames.Count
    frames[i].apply(a.e)
    replay.frames[i].apply(b.e)
    a.update
    a.draw
    b.update
    b.draw
  end
  assert_equal Persistence.json(Persistence.capture(a)), Persistence.json(Persistence.capture(b))
  assert a.e.pixels.SequenceEqual(b.e.pixels)
  assert_equal a.objects.Count, b.objects.Count
  for i in 0...a.objects.Count
    assert_equal a.objects[i].kind, b.objects[i].kind
    assert_equal a.objects[i].x, b.objects[i].x
    assert_equal a.objects[i].y, b.objects[i].y
    assert_equal a.objects[i].vx, b.objects[i].vx
    assert_equal a.objects[i].vy, b.objects[i].vy
  end
end
