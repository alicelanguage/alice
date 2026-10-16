# Headless update-only benchmark: 3600 scripted ticks (2 min at 30 Hz), median of 7 trials, bytes allocated per trial.
require "../../samples/celeste/src/pico"
require "../../samples/celeste/src/classic"
require "../../samples/celeste/src/objects"

def script(e : Pico, t : Int32)
  phase = t % 240
  e.buttons[0] = phase >= 150 && phase < 170
  e.buttons[1] = phase < 120
  e.buttons[2] = phase >= 100 && phase < 110
  e.buttons[3] = false
  e.buttons[4] = phase % 40 < 6
  e.buttons[5] = phase % 90 == 30
end

class Trial
  getter ms : Float64 = 0.0
  getter bytes : Int64 = 0_i64
  getter check : Float64 = 0.0

  def initialize(ticks : Int32)
    e = Pico.new(17)
    g = Classic.new(e)
    g.begin_game
    b0 = GC.GetAllocatedBytesForCurrentThread
    sw = System.Diagnostics.Stopwatch.StartNew
    for t in 0...ticks
      script(e, t)
      g.update
    end
    @ms = sw.Elapsed.TotalMilliseconds
    @bytes = GC.GetAllocatedBytesForCurrentThread - b0
    @check = g.deaths.to_f * 1000.0 + g.room_x * 100.0 + g.room_y * 10.0 + g.frames
  end
end

check = Trial.new(3600).check
for w in 0...3
  Trial.new(3600)
end
times = Array(Float64).new(7)
bytes = 0_i64
for r in 0...7
  tr = Trial.new(3600)
  times[r] = tr.ms
  bytes = tr.bytes
  raise "nondeterministic" if tr.check != check
end
System.Array.Sort(times)
puts "RESULT celeste_update #{times[3]} ms  #{bytes} bytes/trial  check=#{check}"
