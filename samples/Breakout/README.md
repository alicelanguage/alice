# Breakout

![Breakout gameplay](../../docs/media/breakout.gif)

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

## Demo capture

`ALICE_BREAKOUT_AUTOPILOT=1 alice run Breakout` plays itself. `ALICE_BREAKOUT_RECORD=FILE` (with
`ALICE_BREAKOUT_FRAMES=N`, default 600) also writes raw 800x600 RGBA frames and quits; the README GIF was made with

```bash
mkfifo rec && ALICE_BREAKOUT_RECORD=rec ALICE_BREAKOUT_FRAMES=1800 alice run Breakout &
ffmpeg -f rawvideo -pixel_format rgba -video_size 800x600 -framerate 60 -i rec \
  -vf "setpts=PTS/2.5,fps=25,scale=560:-1,split[a][b];[a]palettegen=max_colors=96[p];[b][p]paletteuse" breakout.gif
```
