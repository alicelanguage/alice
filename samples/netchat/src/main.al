# netchat: a TCP chat room. One hub task owns the room; one task per connection reads lines;
# they talk through a Channel(Event), so no socket or table is shared between threads.
#
#   alice run -- server 7000            run a server on 127.0.0.1:7000
#   alice run -- client 127.0.0.1 7000  chat from stdin (first line: /nick NAME)
#   alice run -- demo                   server + two scripted clients in one process, prints a transcript
require "./protocol"
require "./server"
require "./client"

def demo
  server = ChatServer.new(0)
  server.start
  alice = ChatClient.new("127.0.0.1", server.port)
  alice.wait_for("* welcome, set a name with /nick NAME")
  alice.say("/nick alice")
  alice.wait_for(Protocol.joined("alice"))
  bob = ChatClient.new("127.0.0.1", server.port)
  bob.wait_for("* welcome, set a name with /nick NAME")
  bob.say("hello?")
  puts "bob   <- #{bob.receive}"
  bob.say("/nick bob")
  puts "bob   <- #{bob.wait_for(Protocol.joined("bob")).last}"
  alice.wait_for(Protocol.joined("bob"))
  alice.say("hi bob")
  puts "bob   <- #{bob.wait_for(Protocol.said("alice", "hi bob")).last}"
  bob.say("/who")
  puts "bob   <- #{bob.receive}"
  alice.say("/quit")
  puts "alice <- #{alice.wait_for("* bye").last}"
  puts "bob   <- #{bob.wait_for(Protocol.left("alice")).last}"
  alice.close
  bob.close
  server.stop
  puts "demo done"
end

def interactive_client(host : String, port : Int32)
  client = ChatClient.new(host, port)
  inbox = client.inbox
  printer = spawn { inbox.each { |line| puts line } }
  quit = false
  while line = gets
    client.say(line)
    if line.strip == "/quit"
      quit = true
      break
    end
  end
  client.say("/quit") if !quit   # end of input
  printer.wait                     # the server says bye and closes the connection
  client.close
end

mode = ARGV.size > 0 ? ARGV[0] : "demo"
case mode
when "server"
  port = ARGV.size > 1 ? Int32.Parse(ARGV[1]) : 7000
  server = ChatServer.new(port)
  server.start
  puts "netchat listening on 127.0.0.1:#{server.port} (Ctrl-C to stop)"
  System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite)
when "client"
  interactive_client(ARGV.size > 1 ? ARGV[1] : "127.0.0.1", ARGV.size > 2 ? Int32.Parse(ARGV[2]) : 7000)
else
  demo
end
