#!/usr/bin/env bash
# Renders the PNG/ICO exports from the SVG sources.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p png
for s in 16 32 48 64 128 256 512 1024; do rsvg-convert -w $s -h $s alice-icon.svg -o png/alice-icon-$s.png; done
rsvg-convert -w 512 alice-mark.svg -o png/alice-mark-512.png
rsvg-convert -w 1800 alice-logo.svg -o png/alice-logo.png
rsvg-convert -w 1800 alice-logo-dark.svg -o png/alice-logo-dark.png
rsvg-convert -w 1280 alice-social.svg -o png/alice-social.png
rsvg-convert -w 500 alice-card.svg -o png/alice-card.png
magick png/alice-icon-16.png png/alice-icon-32.png png/alice-icon-48.png png/alice-icon-256.png alice.ico
