# Pong on raylib

Pong in Alice with [Raylib-cs](https://github.com/ChrisDill/Raylib-cs). The rules are a plain class with no
graphics, so they are tested headless and their integer helpers are proven; the window code only reads keys and
draws.

| File | Role |
|---|---|
| [src/pong.al](src/pong.al) | `Pong`: ball, paddles, walls, scoring, the opponent; `pure` helpers `clamp_paddle`, `hit_zone`, `follow` |
| [src/main.al](src/main.al) | The raylib window loop, and `--simulate N` without a window |
| [test/pong_test.al](test/pong_test.al) | Serve, paddles on screen, bounces, scoring, speed-up |
| [RULES.al](RULES.al) | Four rules over every case of their domains; `PROOF.al` is the result |

## Run

```bash
cd samples/raylib-pong
alice run                       # W/S or Up/Down; the right paddle plays itself; Esc quits
alice run -- --simulate 3600    # 60 seconds of both paddles playing, no window
```

```text
after 3600 frames: left 0, right 2, paddle hits 32
```

The window needs a display; `--simulate` and the tests do not.

## Test and prove

```bash
alice test                      # 7 examples, 0 failures
alice prove RULES.al            # writes PROOF.al; --check verifies it is current
```

```text
clamp_is_stable                     2,001 cases  proven
follow_moves_towards_the_ball     167,321 cases  proven
hit_zone_is_bounded_and_symmetric  22,631 cases  proven
paddle_stays_on_screen              2,001 cases  proven
```

The prover found a real bug while this sample was written: `hit_zone` was `offset * 4 / 40`, and Alice's `/`
rounds down, so a hit 1 pixel above the centre steered the ball up while 1 pixel below did not steer it. The
symmetric version computes the zone on the distance and puts the sign back.

Notes:

- The `pure` helpers spell sizes as numbers (`370` for `HEIGHT - PADDLE_H`): reading a constant inside a
  `pure def` is reported as impure (AL0210) today. `test_pure_helpers_match_the_constants` keeps them in step.
- Run `alice prove RULES.al`, not plain `alice prove`: without the file argument the prover compiles every
  source, including `main.al`, without the package's NuGet references, and stops at `Raylib`.
- raylib's `IsKeyDown` returns `CBool`; `key?` converts it to `Bool` before `||`.
