module Film
  def self.frames(fps : Int32) : Int32
    fps * 30
  end

  def self.time(frame : Int32, fps : Int32) : Float64
    frame.to_f / fps
  end

  def self.chapter(t : Float64) : Int32
    t < 10.0 ? 0 : (t < 20.0 ? 1 : 2)
  end

  def self.ease(t : Float64) : Float64
    t * t * (3.0 - 2.0 * t)
  end
end
