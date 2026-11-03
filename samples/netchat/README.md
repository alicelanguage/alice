# netchat: a TCP chat room

A chat server and client over plain TCP, written with `spawn` and `Channel`. One hub task owns the room (who is
connected, under which name); one task per connection reads lines and sends them to the hub as events. No
socket, table or counter is shared between threads, so there is no lock in the program, and the compiler's race
check (AL0202) has nothing to reject.

| File | Role |
|---|---|
| [src/protocol.al](src/protocol.al) | The line protocol: `Command` sum type, `Protocol.parse`, the messages the server sends |
| [src/server.al](src/server.al) | `ChatServer`: accept loop, one reader task per connection, the hub |
| [src/client.al](src/client.al) | `ChatClient`: a writer, and a reader task that fills an inbox `Channel(String)` |
| [src/main.al](src/main.al) | `server`, `client` and `demo` modes |
| [test/chat_test.al](test/chat_test.al) | Protocol parsing, and two real clients chatting over loopback |

## How it works

```
reader task (per connection) --Event::Connected/Line/Closed--> Channel(Event) --> hub task --> StreamWriter.WriteLine
```

- `Channel(Event).new(64)` is a buffered channel; `events.each` in the hub takes one event at a time, so the
  hub's `Hash` tables are only touched by the hub.
- The hub is the only writer to any socket. A reader that wants to answer its own client (for example "set a
  name first") sends an event; the hub writes the answer.
- Line length: a message is cut to 400 characters, names are 1 to 16 letters, digits or `_`. `StreamReader.ReadLine`
  itself is unbounded, so a hostile client can still send one huge line; put a length-limited reader in front for
  untrusted networks.
- The server listens on 127.0.0.1 only.

Protocol: the first line should be `/nick NAME`. Then any line is a message to the room; `/who` lists who is
online; `/quit` leaves.

## Run

From this directory (`alice` is the CLI; in this checkout, `dotnet run --project ../../src/Alice.Cli --` works too):

```bash
alice run -- demo
```

Expected output (a server and two scripted clients in one process):

```text
bob   <- * set a name first: /nick NAME
bob   <- * bob joined
bob   <- <alice> hi bob
bob   <- * online: alice, bob
alice <- * bye
bob   <- * alice left
demo done
```

Two terminals:

```bash
alice run -- server 7000
alice run -- client 127.0.0.1 7000      # type /nick alice, then messages; /quit to leave
```

A client reads from stdin, so it can be scripted:

```bash
printf '/nick zed\nhi all\n/quit\n' | alice run -- client 127.0.0.1 7000
```

```text
* welcome, set a name with /nick NAME
* zed joined
<zed> hi all
* bye
```

## Test

```bash
alice test
```

Six specs: parsing, and an end-to-end chat between two `ChatClient`s and a `ChatServer` on a free port.
`examples/verify.sh` and `SampleTests.Netchat_tcp_server_and_clients_talk_through_channels` run the demo and the specs.
