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

All sources live in `src/`; `alice test` builds the project and compiles `test/*.al` against its references (MonoGame included).
