require "./world"

def wonderland : World
  World.build do |w|
    w.room "rabbit hole" do |r|
      r.instance_eval do
        describe "You land on a heap of dry leaves. A tunnel runs east; a tiny door glows below."
        exit "east", "hall"
        exit "down", "garden"
        item "watch"
      end
    end
    w.room "hall" do |r|
      r.instance_eval do
        describe "A long, low hall lit by lamps. On a glass table lies a small golden key."
        exit "west", "rabbit hole"
        item "key"
        item "bottle"
      end
    end
    w.room "garden" do |r|
      r.instance_eval do
        describe "Roses painted red, still dripping. Cards march past. A path leads north."
        locked "key"
        exit "up", "rabbit hole"
        exit "north", "tea party"
      end
    end
    w.room "tea party" do |r|
      r.instance_eval do
        describe "A long table, a Hatter, a March Hare and a sleepy Dormouse. \"No room! No room!\" — but there is."
        exit "south", "garden"
      end
    end
  end
end
