using System.IO
using System.Net
using System.Net.Sockets
using System.Text

# What a connection reader tells the hub. Only the hub writes to sockets and owns the room state.
enum Event
  Connected(id : Int32, writer : StreamWriter)
  Line(id : Int32, command : Command)
  Closed(id : Int32)
end

class ChatServer
  getter port : Int32

  def initialize(port : Int32)
    @listener = TcpListener.new(IPAddress.Loopback, port)
    @listener.Start()
    @port = @listener.LocalEndpoint.as(IPEndPoint).Port
    @events = Channel(Event).new(64)
  end

  # Starts the hub and the accept loop; returns at once. `stop` closes the listener.
  def start
    events = @events
    listener = @listener
    spawn { ChatServer.hub(events) }
    spawn { ChatServer.accept_loop(listener, events) }
  end

  def stop
    @listener.Stop()
  end

  def self.accept_loop(listener : TcpListener, events : Channel(Event))
    next_id = 0
    begin
      while true
        client = listener.AcceptTcpClient()
        next_id += 1
        id = next_id
        spawn { ChatServer.read_client(id, client, events) }
      end
    rescue e : SocketException
      # the listener was stopped
    rescue e : ObjectDisposedException
    end
  end

  # One task per connection: decode lines, forward them as events. It never writes to a socket.
  def self.read_client(id : Int32, client : TcpClient, events : Channel(Event))
    stream = client.GetStream()
    writer = StreamWriter.new(stream, UTF8Encoding.new(false))
    writer.NewLine = "\n"
    writer.AutoFlush = true
    events.send(Event::Connected(id, writer))
    reader = StreamReader.new(stream, Encoding.UTF8)
    begin
      while line = reader.ReadLine()
        command = Protocol.parse(line)
        events.send(Event::Line(id, command))
        break if command == Command::Quit
      end
    rescue e : IOException
      # the peer reset the connection
    end
    events.send(Event::Closed(id))
  end

  # The only owner of the room: connection id -> writer and id -> name. Events arrive one at a time.
  def self.hub(events : Channel(Event))
    writers = Hash(Int32, StreamWriter).new
    names = Hash(Int32, String).new
    events.each do |event|
      case event
      when Event::Connected(id, writer)
        writers[id] = writer
        ChatServer.write(writer, "* welcome, set a name with /nick NAME")
      when Event::Line(id, command)
        ChatServer.handle(id, command, writers, names)
      when Event::Closed(id)
        writers[id].Dispose()   # closes the socket: the client sees end of stream
        writers.delete(id)
        if names.key?(id)
          name = names[id]
          names.delete(id)
          ChatServer.broadcast(writers, names, Protocol.left(name))
        end
      end
    end
  end

  def self.handle(id : Int32, command : Command, writers : Hash(Int32, StreamWriter), names : Hash(Int32, String))
    writer = writers[id]
    case command
    when Command::Nick(name)
      if names.values.includes?(name)
        ChatServer.write(writer, "* #{name} is taken")
      elsif names.key?(id)
        ChatServer.write(writer, "* you are #{names[id]}")
      else
        names[id] = name
        ChatServer.broadcast(writers, names, Protocol.joined(name))
      end
    when Command::Say(text)
      if names.key?(id)
        ChatServer.broadcast(writers, names, Protocol.said(names[id], text))
      else
        ChatServer.write(writer, "* set a name first: /nick NAME")
      end
    when Command::Who
      ChatServer.write(writer, Protocol.online(names.values.to_a))
    when Command::Quit
      ChatServer.write(writer, "* bye")
    when Command::Invalid(reason)
      ChatServer.write(writer, "* #{reason}")
    end
  end

  # Named clients only: a connection joins the room with /nick.
  def self.broadcast(writers : Hash(Int32, StreamWriter), names : Hash(Int32, String), line : String)
    for id in names.keys.to_a
      ChatServer.write(writers[id], line)
    end
  end

  def self.write(writer : StreamWriter, line : String)
    begin
      writer.WriteLine(line)
    rescue e : IOException
      # a client that went away is removed by its Closed event
    rescue e : ObjectDisposedException
    end
  end
end
