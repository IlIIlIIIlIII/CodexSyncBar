#!/usr/bin/env python3
"""Render the Ubuntu launcher PNG from its editable SVG using system librsvg."""
from pathlib import Path

import cairo
import gi

gi.require_version('Rsvg', '2.0')
from gi.repository import Rsvg

root = Path(__file__).resolve().parents[2]
source = root / 'Ubuntu/resources/codex-syncbar.svg'
surface = cairo.ImageSurface(cairo.FORMAT_ARGB32, 256, 256)
handle = Rsvg.Handle.new_from_file(str(source))
viewport = Rsvg.Rectangle()
viewport.x = viewport.y = 0
viewport.width = viewport.height = 256
handle.render_document(cairo.Context(surface), viewport)
surface.write_to_png(str(source.with_suffix('.png')))
