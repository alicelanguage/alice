using System.Text.Json
require "./classic"

# Records provide the JSON constructor, named fields and value equality automatically.
# Saves are room checkpoints, so private animation/collision state never leaks into the format.
record Progress(version : Int32, room : Int32, deaths : Int32, minutes : Int32, seconds : Int32, max_dashes : Int32, new_bg : Bool, berries : Array(Int32), completed : Bool)
record struct InputFrame(left : Bool, right : Bool, up : Bool, down : Bool, jump : Bool, dash : Bool)
  def self.capture(e : Pico) : InputFrame
    InputFrame.new(e.btn(0), e.btn(1), e.btn(2), e.btn(3), e.btn(4), e.btn(5))
  end

  def apply(e : Pico)
    e.buttons[0] = left
    e.buttons[1] = right
    e.buttons[2] = up
    e.buttons[3] = down
    e.buttons[4] = jump
    e.buttons[5] = dash
  end
end
record Replay(version : Int32, seed : Int32, frames : Array(InputFrame))

module Persistence
  def self.json(value) : String
    # Untyped parameter is specialized for Progress and Replay by Alice, without dynamic dispatch.
    options = JsonSerializerOptions.new
    options.WriteIndented = true
    JsonSerializer.Serialize(value, options)
  end

  def self.capture(g : Classic) : Progress
    Progress.new(1, g.level_index, g.deaths, g.minutes, g.seconds, g.max_djump, g.new_bg, g.got_fruit.OrderBy(->(n : Int32) { n }).ToArray, g.completed)
  end

  def self.progress(text : String) : Progress
    raw = JsonSerializer.Deserialize(text, typeof(Progress))
    raise "Empty progress file" if raw.nil?
    save = raw.as(Progress)
    raise "Unsupported progress version" if save.version != 1
    raise "Invalid room" if save.room < 0 || save.room > 30
    raise "Invalid dash upgrade" if save.max_dashes < 1 || save.max_dashes > 2
    raise "Invalid statistics" if save.deaths < 0 || save.minutes < 0 || save.seconds < 0 || save.seconds > 59
    raise "Invalid berries" if save.berries.nil?
    save.berries.each { |n| raise "Invalid berry room" if n < 1 || n > 30 }
    save
  end

  def self.restore(g : Classic, save : Progress)
    g.got_fruit.Clear()
    save.berries.each { |n| g.got_fruit.Add(n) }
    g.deaths = save.deaths
    g.minutes = save.minutes
    g.seconds = save.seconds
    g.frames = 0
    g.max_djump = save.max_dashes
    g.new_bg = save.new_bg
    g.completed = save.completed
    g.pause_player = false
    g.flash_bg = false
    g.freeze = 0
    g.shake = 0
    g.e.camera
    g.load_room(save.room % 8, save.room / 8)
  end

  def self.replay(text : String) : Replay
    raw = JsonSerializer.Deserialize(text, typeof(Replay))
    raise "Empty replay file" if raw.nil?
    replay = raw.as(Replay)
    raise "Unsupported replay version" if replay.version != 1
    raise "Invalid replay frames" if replay.frames.nil?
    raise "Replay exceeds one hour" if replay.frames.Length > 108000
    replay
  end

  def self.path(name : String) : String
    Path.Combine(Assets.root, "..", ".celeste", name + ".json")
  end

  def self.write(path : String, text : String)
    Directory.CreateDirectory(Path.GetDirectoryName(path).not_nil!)
    # Same-filesystem rename ensures a failed write cannot truncate the previous checkpoint.
    File.WriteAllText(path + ".tmp", text)
    File.Move(path + ".tmp", path, true)
  end
end
