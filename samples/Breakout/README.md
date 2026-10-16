# Breakout

MonoGame (DesktopGL) Breakout written in Alice. No content pipeline: everything is drawn from a 1x1 white texture, text uses a built-in 3x5 bitmap font (`src/font.al`).

## Run

```bash
cd samples && alice run Breakout
```

Headless logic tests:

```bash
cd samples/Breakout && alice test
```

## Controls

| Key | Action |
|---|---|
| Left/Right, A/D, mouse | move paddle |
| Space, left click | launch ball (Enter/Space starts from title) |
| P | pause / resume |
| Enter | restart after game over / win |
| Escape | quit |

## Layout

| Path | Contents |
|---|---|
| `src/physics.al` | `Box` AABB, `Ball`, `Physics` (walls, paddle angle, brick reflect) |
| `src/game.al` | `GameState` / `Brick` sum types, `Input`, `Rules`, `World` (score, lives, 3 levels, speed-up, shake) |
| `src/particles.al` | `@[SoA]` `Spark` store, column-wise vectorized update |
| `src/font.al` | 3x5 glyph table, `Font.each_pixel` |
| `app/render.al` | `Painter`, `Render` (MonoGame drawing) |
| `app/main.al` | `BreakoutGame < Game`: input + loop wiring |
| `test/logic_test.al` | `alice test` checks |

`main.al` and `render.al` live in `app/`, not `src/`: `alice test` compiles `src/` without MonoGame references, so `src/` must stay MonoGame-free. `alice build` compiles every `.al` outside `test/`, `bin/`, `obj/`.
