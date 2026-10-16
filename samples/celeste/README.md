# Celeste Classic — rewritten in Alice

An educational Alice rewrite of **Celeste Classic**, the original PICO-8 mountain-climbing game by **Maddy Thorson and Noel Berry**. It follows the game implementation published in [NoelFB/Celeste](https://github.com/NoelFB/Celeste), with the original room data, sprite atlas, font, movement constants, and object behaviors.

This example demonstrates a real game in Alice: static metaprogramming, generated structure-of-arrays storage, records, typed blocks, JSON serialization, deterministic input replays, and .NET library interop. All game, host, persistence, and test code authored for this sample is `.al`.

## Original project and credits

- **Original repository:** [NoelFB/Celeste](https://github.com/NoelFB/Celeste).
- **Game implementation used for the rewrite:** [Source/PICO-8/Classic.cs](https://github.com/NoelFB/Celeste/blob/1b0ce45c75e05649ae91b44a8bb6b196684e4352/Source/PICO-8/Classic.cs).
- **PICO-8 API behavior and room data:** [Source/PICO-8/Emulator.cs](https://github.com/NoelFB/Celeste/blob/1b0ce45c75e05649ae91b44a8bb6b196684e4352/Source/PICO-8/Emulator.cs).
- **Original graphics:** [Source/PICO-8/Graphics](https://github.com/NoelFB/Celeste/tree/1b0ce45c75e05649ae91b44a8bb6b196684e4352/Source/PICO-8/Graphics).
- **Play the original PICO-8 game and view its Lua:** [Lexaloffle cartridge](https://www.lexaloffle.com/bbs/?tid=2145).
- **Celeste:** [official website](https://www.celestegame.com/).

**All rights to the original Celeste game, name, characters, artwork, and other original materials belong to their respective creators and rights holders.** This is an unofficial educational sample; it claims no ownership of Celeste and is not endorsed by its creators. PICO-8 and its name/logo belong to Lexaloffle Games LLP.

The upstream published source carries the MIT license, copyright © 2018 Noel Berry. Its complete notice is preserved in [LICENSE.upstream](LICENSE.upstream). As the [upstream README](https://github.com/NoelFB/Celeste#source-code) explains, that source-code license does not grant rights to the commercial Celeste game or its assets. The files here come from the linked public PICO-8 source directory; no commercial game installation is included. Keep the original attribution and license notice when redistributing this derivative sample, and consult the original rights holders for rights outside that source-code license.

## Run

Requirements: the **.NET 10 SDK**, Alice, and a graphical desktop supported by the raylib native library shipped with `Raylib-cs` 7.0.2. The first build restores that NuGet package. MonoGame, Monocle, the commercial Celeste game, and a PICO-8 installation are not required.

From the samples workspace:

```bash
cd samples
alice run celeste
```

From this sample directory:

```bash
alice build --release
alice run --release
alice test
alice fmt --check src test
```

Using the compiler from a repository checkout, run these equivalents from the repository root:

```bash
dotnet run --project src/Alice.Cli -- run samples/celeste --release
# Tests need the package directory as their working directory:
cd samples/celeste
dotnet ../../src/Alice.Cli/bin/Debug/net10.0/alice.dll test
```

`alice.toml` is the project definition; Alice generates `.alice/celeste.alproj`. The sample is a normal executable package with a raylib dependency. The window is resizable, and the 128×128 game image is scaled by an integer so its pixels stay sharp.

## Controls

| Key | Action |
|---|---|
| Arrows / WASD | Move; choose dash direction |
| Z / C / Space | Jump; start from the title |
| X / Left Shift | Dash; start from the title |
| P | Pause / resume |
| R | Start a fresh run |
| F5 | Save a checkpoint at the current room |
| F9 | Load the saved checkpoint |
| F6 | Start recording a new run; press again to save the replay |
| F7 | Play the saved input replay |
| Escape | Quit |

A dash without directional input follows the player's facing. Landing, a spring, a balloon, or a strawberry refills dashes. The large chest's orb upgrades the player to two dashes.

The original title's start flash lasts about 2.7 seconds. Replays stop and pause at their last recorded frame; press P to continue playing. Beginning a recording starts a fresh, seeded run so the saved inputs can reproduce it.

## What is included

The original map has **32 screens**: level indices 0–29, the summit at 30, and the title at 31. The rewrite includes the original spawn animation, grounded movement, ice, wall sliding, wall jumps, jump buffering, coyote time, eight-direction dashes, freeze frames, screen shake, and directional spike collisions.

Object behaviors include springs, balloons, crumbling floors, strawberries, flying strawberries, breakable false walls, keys and chests, moving one-way platforms, the memorial, the large chest sequence, the double-dash orb, summit flag, hair, snow, clouds, and death particles.

The upstream implementation changes some state during drawing, including hair animation, room titles, the chest sequence, orb pickup, and summit presentation. This rewrite retains that update/draw ordering: one `update` followed by one `draw` at a target of **30 ticks per second**. Pausing stops both.

The original commercial host's FMOD sound bank, music, achievements, and platform statistics are not part of the public source sample. Sound/music event IDs are preserved in the PICO API, but this desktop host is currently **silent**. Completion is tracked locally.

## Alice features used

### Static method generation

`Entity` uses Alice's `define_method` syntax for real, statically compiled gameplay methods:

```alice
define_method :can_dash? do
  @dashes > 0
end

define_method :grounded? do
  is_solid(0.0, 1.0)
end
```

These methods participate in normal type checking. The `property` and `getter` declarations generate typed accessors. They do not depend on runtime method lookup or `method_missing`.

### Generated structure-of-arrays storage

```alice
@[SoA]
record struct Snow(x : Float64, y : Float64, speed : Float64,
                   phase : Float64, radius : Float64, color : Int32)

getter particles : SoA(Snow) = SoA(Snow).new
```

Alice generates the column storage and row access for `Snow` and `Cloud`. The game updates and renders them using typed blocks. Stateful gameplay entities stay in an ordered list because spawning, destruction, and collision behavior depend on that order.

### Records and JSON

`Rect` and `InputFrame` are value records. `Progress` and `Replay` are named records that serialize through `System.Text.Json`. Alice generates their constructors, accessors, and equality behavior.

`Persistence.json(value)` has an untyped parameter that Alice specializes for the record types actually passed to it. The serialization work itself uses the standard .NET serializer; this sample does not invent a new language-level serializer.

### Blocks and typed dispatch

`Pico.each_tile` yields coordinates and tile IDs to a typed block. Room construction and map drawing reuse that traversal. The original object's runtime `GetType` checks become a typed `Kind` enum, and dispatch uses `case` with explicit cases for each behavior. Effectful game updates stay sequential.

## Checkpoints and deterministic replays

Local files live in **`.celeste/`**, which is ignored by Git:

| File | Contents |
|---|---|
| `progress.json` | Schema version, room, deaths, timer, dash upgrade, collected berry rooms, background state, completion |
| `replay.json` | Schema version, random seed, six held input buttons for each simulation tick |

Checkpoint loading validates the schema version, room, statistics, dash upgrade, and berry IDs before restoring the game. A checkpoint restarts the current room with collected fruit and the upgrade retained; it is not a save of a particular midair animation frame. Writes use a temporary file followed by a same-directory rename.

Replay recording stores input snapshots rather than rendered frames. Both recording and playback start with the same seed, room, and random initialization. The test suite checks that serializing and replaying 240 input frames reproduces entity positions, velocities, statistics, and the final pixel buffer. Recordings are limited to one hour. Cross-version or cross-runtime bitwise replay compatibility is not promised.

## Comparison with the original C#

| Area | Upstream `Classic.cs` / `Emulator.cs` | Alice rewrite |
|---|---|---|
| Game objects | Nested subclasses of `ClassicObject` | Typed `Kind` and `Entity` behavior dispatch |
| Type collision checks | Generic methods and runtime type equality | Explicit `Kind` comparisons and nullable `Entity` results |
| Collision geometry | XNA `Rectangle` / `Vector2` | Alice `Rect` record and typed numeric fields |
| Snow / clouds | Lists of reference objects | `@[SoA]` records and generated column stores |
| Helpers | Explicit methods / fields | Generated predicates, properties, records, typed blocks |
| Graphics host | Celeste / Monocle / XNA | Alice software PICO buffer; raylib presents pixel runs |
| Map / sprites | Embedded map string and PICO atlas | Same map string and downloaded atlas, decoded by Alice |
| Progress | Host-game state / platform integration | Versioned JSON room checkpoints |
| Input replay | No equivalent in the published sample | Seeded Alice input replay |
| Numbers | Primarily `float` / XNA single precision | `Float64` gameplay arithmetic |

The rewrite follows the source's mechanics and constants, including its inclusive pixel movement loops. It is **not a claim of bit-for-bit equivalence**: numeric precision, object representation, completion reporting, and the desktop host differ. The tests exercise individual mechanisms and complete update/draw loops; they are not an automated playthrough of every room to the summit.

## Source map

| File | Responsibility |
|---|---|
| [`src/classic.al`](src/classic.al) | Rooms, run state, update/draw ordering, map and spike collision, effects |
| [`src/objects.al`](src/objects.al) | Player and every original object behavior |
| [`src/pico.al`](src/pico.al) | Map decoding, sprite/font blits, palette, camera, raster primitives, input and event IDs |
| [`src/main.al`](src/main.al) | Window, keyboard, scaling, pause, checkpoint/replay controls |
| [`src/persistence.al`](src/persistence.al) | Save/replay records, JSON, validation and atomic writes |
| [`test/classic_test.al`](test/classic_test.al) | Headless Alice gameplay and serialization tests |
| [`assets/provenance.json`](assets/provenance.json) | Upstream revision, source URLs and SHA-256 hashes |

## Assets

All four original PNGs are downloaded unchanged into [`assets/upstream/`](assets/upstream/): `atlas.png`, `font.png`, `logo.png`, and `consolebg.png`. The game uses the atlas and font; the console background/logo are retained with the downloaded source assets but are not required by the desktop presentation.

`atlas.pixels` and `font.pixels` are text representations of palette indices and alpha (`.` means transparent), derived from those PNGs for the managed software renderer. `map.hex` preserves the original embedded map data. As in the original emulator, the lower half's hexadecimal nibbles are reversed during decoding. SHA-256 hashes and exact download URLs are in `provenance.json`.

## Validation and design

`alice test` runs 28 headless tests covering original map/object coverage, generated stores and predicates, jump grace/buffering, wall movement, all dash directions, collision tunneling, directional spikes, death/respawn, springs, balloons, crumbling floors, fruit persistence, flying fruit, keys/chests, false walls, moving platforms, the orb, room progression, the summit flag, JSON validation, and deterministic replay.

This sample requires no changes to the Alice compiler or runtime.
