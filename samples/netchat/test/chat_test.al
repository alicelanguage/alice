require "../src/protocol"
require "../src/server"
require "../src/client"

def test_plain_text_is_a_message
  assert_equal Command::Say("hello there"), Protocol.parse("  hello there  ")
end

def test_nick_needs_a_valid_name
  assert_equal Command::Nick("alice_1"), Protocol.parse("/nick alice_1")
  assert_equal Command::Invalid("names are 1-16 letters, digits or _"), Protocol.parse("/nick bad name!")
  assert_equal Command::Invalid("names are 1-16 letters, digits or _"), Protocol.parse("/nick")
  assert !Protocol.valid_name?("a_name_that_is_too_long")
end

def test_commands
  assert_equal Command::Who, Protocol.parse("/who")
  assert_equal Command::Quit, Protocol.parse("/quit")
  assert_equal Command::Invalid("unknown command /dance"), Protocol.parse("/dance now")
  assert_equal Command::Invalid("empty message"), Protocol.parse("   ")
end

def test_long_messages_are_cut
  case Protocol.parse("x" * 1000)
  when Command::Say(text) then assert_equal Protocol::MAX_TEXT, text.size
  else assert false, "expected a message"
  end
end

def test_online_list_is_sorted
  assert_equal "* online: alice, bob, carol", Protocol.online(["carol", "alice", "bob"])
end

def test_two_clients_chat_over_tcp
  server = ChatServer.new(0)
  server.start
  a = ChatClient.new("127.0.0.1", server.port)
  b = ChatClient.new("127.0.0.1", server.port)
  a.say("/nick ann")
  a.wait_for("* ann joined")
  b.say("/nick ann")
  assert_equal "* ann is taken", b.wait_for("* ann is taken").last
  b.say("/nick ben")
  a.wait_for("* ben joined")
  b.wait_for("* ben joined")
  a.say("ping")
  assert_equal "<ann> ping", b.wait_for("<ann> ping").last
  b.say("/quit")
  assert_equal "* ben left", a.wait_for("* ben left").last
  a.close
  b.close
  server.stop
end
