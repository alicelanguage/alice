# A tiny adventure engine whose worlds are written as a Ruby-style DSL:
#   world.room "hall" do
#     describe "A long hall of doors."
#     exit "north", "garden"
#     item "key"
#   end
class Room
  getter name : String
  getter text : String = ""
  getter exits : Dictionary(String, String) = Dictionary(String, String).new
  getter items : List(String) = List(String).new
  property locked_by : String = ""

  def initialize(@name : String)
  end

  # DSL verbs, called with the room as the implicit receiver (instance_eval)
  def describe(t : String)
    @text = t
  end

  def exit(dir : String, to : String)
    @exits[dir] = to
  end

  def item(n : String)
    @items.Add(n)
  end

  def locked(by : String)
    @locked_by = by
  end
end

class World
  getter rooms : Dictionary(String, Room) = Dictionary(String, Room).new
  getter start : String = ""

  def room(name : String)
    r = Room.new(name)
    @start = name if @rooms.Count == 0
    @rooms[name] = r
    yield r
  end

  def self.build
    w = World.new
    yield w
    w
  end
end

class Game
  getter world : World
  getter here : String
  getter bag : List(String) = List(String).new
  getter moves : Int32 = 0
  getter won : Bool = false

  def initialize(@world : World)
    @here = world.start
  end

  def room : Room
    @world.rooms[@here]
  end

  def look : String
    r = room
    s = "#{r.name.upcase}\n#{r.text}"
    s += "\nYou see: #{r.items.join(", ")}" if r.items.Count > 0
    s + "\nExits: #{r.exits.Keys.join(", ")}"
  end

  # Commands: look, go DIR, take ITEM, bag, or just a direction (n/s/e/w/up/down).
  def command(line : String) : String
    @moves += 1
    words = line.strip.downcase.split(" ")
    case words
    in ["look"] | ["l"]
      look
    in ["bag"] | ["i"]
      @bag.Count == 0 ? "Your bag is empty." : "You carry: #{@bag.join(", ")}"
    in ["take", thing]
      take(thing)
    in ["go", dir]
      go(dir)
    in [dir]
      go(dir)
    else
      "The Cheshire Cat grins: \"#{line}\"? Try look, go DIR, take ITEM, bag."
    end
  end

  def take(thing : String) : String
    return "There is no #{thing} here." unless room.items.Contains(thing)
    room.items.Remove(thing)
    @bag.Add(thing)
    "Taken: #{thing}."
  end

  def go(dir : String) : String
    return "You can't go #{dir}." unless room.exits.ContainsKey(dir)
    target = @world.rooms[room.exits[dir]]
    if target.locked_by != "" && !@bag.Contains(target.locked_by)
      return "The way #{dir} is locked. You need the #{target.locked_by}."
    end
    @here = target.name
    @won = true if @here == "tea party"
    look
  end
end
