using System.IO
using System.Net.Sockets
using System.Text

# A connection to a chat server. Lines from the server arrive on `inbox`, filled by one reader task.
class ChatClient
  getter inbox : Channel(String)

  def initialize(host : String, port : Int32)
    @tcp = TcpClient.new(host, port)
    stream = @tcp.GetStream()
    @writer = StreamWriter.new(stream, UTF8Encoding.new(false))
    @writer.NewLine = "\n"
    @writer.AutoFlush = true
    @inbox = Channel(String).new(64)
    inbox = @inbox
    reader = StreamReader.new(stream, Encoding.UTF8)
    spawn { ChatClient.pump(reader, inbox) }
  end

  def self.pump(reader : StreamReader, inbox : Channel(String))
    begin
      while line = reader.ReadLine()
        inbox.send(line)
      end
    rescue e : IOException
    rescue e : ObjectDisposedException
    end
    inbox.close
  end

  def say(line : String)
    @writer.WriteLine(line)
  end

  # Waits for the next line from the server.
  def receive : String
    @inbox.receive
  end

  # Reads lines until one equals `expected`; returns every line read, in order.
  def wait_for(expected : String) : Array(String)
    seen = List(String).new
    while true
      line = receive
      seen.Add(line)
      break if line == expected
    end
    seen.to_a
  end

  def close
    @tcp.Close()
  end
end
