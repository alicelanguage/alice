# ADR 0012: Alice-authored Wonderland3D film sample

Status: Accepted

## Context

The Wonderland3D sample must demonstrate Alice through a reproducible thirty-second psychedelic 3D video, with a local interactive preview.

## Decision

Use Alice for procedural geometry, camera choreography, deterministic frame timing, audio synthesis, capture and process orchestration. Reuse Raylib-cs 7.0.2, already used by Celeste, for desktop OpenGL rendering. Use GLSL for vertex/fragment shading and bloom, and an external FFmpeg executable for H.264/AAC encoding. Keep `alice.toml` as the project source of truth.

## Consequences

No compiler/runtime changes or handwritten C# are needed. Export requires a graphical OpenGL 3.3 session and FFmpeg with libx264. The sample retains captured frames if encoding fails and removes them after success. Render time is independent of the fixed thirty-second output duration. Interactive preview is silent; the exported film has an original Alice-synthesized score.
