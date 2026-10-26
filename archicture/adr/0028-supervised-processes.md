# ADR 0028: Supervised, isolated processes

Status: Accepted (0.13.2). Amends ADR 0005 ("no process isolation like BEAM").

Context: `Actor(T)` was an object with a `System.Threading.Channels` mailbox and a consumer `Task`. A handler
exception faulted that task silently; nothing linked, monitored or restarted actors; and nothing stopped two actors
from sharing a mutable list (only writes were checked, by AL0202). The goal is BEAM/OTP fault tolerance with
isolation the compiler proves, on the CLR thread pool and without new syntax.

Decision:

- Runtime (`src/Alice.Runtime/Actors/`). `ProcessCore` is the process: a lock-free `ConcurrentQueue<T>` mailbox (its
  ring segment is reused while the consumer keeps up, so steady state allocates nothing) and an `IThreadPoolWorkItem`
  that is queued by a send only when the process is idle (one CAS on a flag). A turn handles up to 128 messages and
  requeues, for fairness. An idle process holds no thread.
- Let it crash. A handler exception kills only its process (Alive -> Dead once, mailbox dropped) and propagates the
  `ExitReason` to `on_exit` monitors, to linked processes (which die with `linked`, unless they are supervisors,
  which trap exits) and to its supervisor. Normal exits (`stop`, `finish`) do not travel along links.
- `Supervisor.one_for_one` / `one_for_all(max_restarts, within_ms)` restart children in place: the same object, an
  empty mailbox and, for `Actor(S, T)`, state rebuilt by `init`, so existing references keep working (the role of a
  registered name in OTP). One consumer is guaranteed across restarts: a restart waits for the old turn to end.
  More than `max_restarts` in the window: the supervisor exits with `max_restarts`, shutting its children down, and
  its own supervisor sees a failure (trees nest). Children are transient: a normal exit removes them.
- `Actor(S, T).new(init, handler)` is the GenServer form: the handler returns the next state; `finish` drains, stops
  and returns it. Delivery around a crash is at-most-once: queued and in-flight messages of the dead incarnation
  are dropped and counted in `dropped_messages`.
- Compile-time isolation (`Binder.Actors`). AL0230: the message type must be sendable (numbers, Bool, Char, String,
  enums, structs, records and classes nothing can change after construction, with sendable fields; tuples;
  `System.Collections.Immutable`; thread-safe handles such as actors, `Channel`, `Atomic`). AL0231: actor blocks
  (`init` and handler) may not capture locals whose type is neither sendable nor thread-safe. State is built inside
  the actor, so it is owned by the process and may be any type. Writes to captured state remain AL0202, and
  `on_exit` blocks are concurrent code.

Consequences: no new keywords; existing `Actor(T)` programs keep working (same `send`/`stop`/`stop_async`). Isolation
is by type, not by copying: a sendable value has no aliased mutable part, so passing the reference is safe. Not BEAM:
no preemption (a handler that loops forever holds a pool thread), no distribution, no selective receive, and a
process cannot be killed in the middle of a handler (a kill takes effect when the current message returns).
Measured: 0 bytes per message steady state; ~6 M msgs/s into one actor, ~11 M msgs/s across 64 (`ActorTests`).
