# Original procedural score: a pentatonic music box over a slowly moving pad.
# PCM synthesis and WAV serialization use only Alice and the .NET library.
module Music
  def self.write(path : String)
    rate = 44100
    count = rate * 30
    stream = File.Create(path)
    writer = BinaryWriter.new(stream)
    begin
      writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"))
      writer.Write(36 + count * 4)
      writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "))
      writer.Write(16)
      writer.Write(Convert.ToInt16(1))
      writer.Write(Convert.ToInt16(2))
      writer.Write(rate)
      writer.Write(rate * 4)
      writer.Write(Convert.ToInt16(4))
      writer.Write(Convert.ToInt16(16))
      writer.Write(System.Text.Encoding.ASCII.GetBytes("data"))
      writer.Write(count * 4)
      notes = [0, 7, 12, 15, 19, 24, 19, 15, 12, 7, 3, 10, 15, 22, 19, 7]
      for i in 0...count
        t = i.to_f / rate
        beat = (t / 0.3125).to_i
        age = t - beat * 0.3125
        f = 293.6648 * Math.Pow(2.0, notes[beat % notes.Length] / 12.0)
        env = (1.0 - Math.Exp(-age * 120.0)) * Math.Exp(-age * 7.0)
        bell = (Math.Sin(Math::PI * 2.0 * f * age) + 0.25 * Math.Sin(Math::PI * 2.0 * f * 2.002 * age)) * env * 0.18
        pad = 0.07 * (Math.Sin(t * Math::PI * 2.0 * 146.8324) + Math.Sin(t * Math::PI * 2.0 * 220.0) + Math.Sin(t * Math::PI * 2.0 * 349.2282))
        pad *= 0.65 + Math.Sin(t * 0.42) * 0.20
        fade = Math.Min(1.0, t / 1.5) * Math.Min(1.0, (30.0 - t) / 2.5)
        pan = Math.Sin(beat * 0.7) * 0.35
        left = (pad + bell * (1.0 - pan)) * fade
        right = (pad + bell * (1.0 + pan)) * fade
        writer.Write(Convert.ToInt16(left * 26000.0))
        writer.Write(Convert.ToInt16(right * 26000.0))
      end
    ensure
      writer.Dispose()
      stream.Dispose()
    end
  end
end
