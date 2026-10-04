#!/usr/bin/env python3
"""Capture an owned GTK fixture; never connects to the production service."""
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'ui'))
from live_gtk_apply import capture_window, Gtk
from gi.repository import Gdk, GdkPixbuf, GLib


class CaptureTests(unittest.TestCase):
    def test_owned_window_waits_for_mapping_and_writes_real_private_png(self):
        Gtk.init()
        if Gdk.Display.get_default() is None:
            self.skipTest('A display session is required for owned GTK rendering.')
        with tempfile.TemporaryDirectory(prefix='syncbar-gtk-capture-') as directory:
            window = Gtk.Window(title='SyncBar isolated capture fixture')
            window.set_default_size(320, 160)
            window.set_child(Gtk.Label(label='격리된 GTK 캡처 검증'))
            self.assertIsNone(capture_window(window, directory, 99))
            result, errors = [], []
            loop = GLib.MainLoop()
            def capture():
                try:
                    path = capture_window(window, directory, 99)
                    if not path:
                        return True
                    result.append(Path(path))
                    class EmptyPaintable:
                        def snapshot(self, _snapshot, _width, _height):
                            pass
                    diagnostics = {}
                    with patch.object(Gtk.WidgetPaintable, 'new', return_value=EmptyPaintable()):
                        fallback = capture_window(window, directory, 99, diagnostics)
                    self.assertIsNotNone(fallback, 'GTK child snapshot should not require a compositor frame')
                    self.assertEqual('Gtk.Widget.snapshot_child', diagnostics['source'])
                    with patch.object(Gtk.WidgetPaintable, 'new', return_value=EmptyPaintable()), patch.object(window, 'snapshot_child', return_value=None):
                        self.assertIsNone(capture_window(window, directory, 99, {}), 'Window background without a child render node is not valid evidence')
                    self.assertEqual(b'\x89PNG\r\n\x1a\n', Path(fallback).read_bytes()[:8])
                    normal = GdkPixbuf.Pixbuf.new_from_file(path)
                    alternate = GdkPixbuf.Pixbuf.new_from_file(fallback)
                    self.assertEqual((normal.get_width(), normal.get_height()),
                                     (alternate.get_width(), alternate.get_height()))
                    def background_pixel(pixbuf):
                        offset = (pixbuf.get_height() // 2) * pixbuf.get_rowstride() + 12 * pixbuf.get_n_channels()
                        return tuple(pixbuf.get_pixels()[offset:offset + pixbuf.get_n_channels()])
                    # Dropping the window CSS background leaves transparent
                    # pixels and washes translucent native controls to white.
                    self.assertEqual(background_pixel(normal), background_pixel(alternate))
                    if alternate.get_has_alpha():
                        self.assertEqual(255, background_pixel(alternate)[3])
                    window.set_visible(False)
                    hidden = {}
                    self.assertIsNone(capture_window(window, directory, 99, hidden))
                    self.assertFalse(hidden['mapped'])
                except BaseException as error:
                    errors.append(error)
                loop.quit()
                return False
            def timeout():
                loop.quit()
                return False
            window.present()
            source = GLib.timeout_add(300, capture)
            expiry = GLib.timeout_add_seconds(5, timeout)
            try:
                loop.run()
                self.assertFalse(errors, errors)
                self.assertEqual(1, len(result), 'GTK did not produce a rendered capture')
                self.assertEqual(b'\x89PNG\r\n\x1a\n', result[0].read_bytes()[:8])
                self.assertEqual(0o600, result[0].stat().st_mode & 0o777)
            finally:
                if GLib.MainContext.default().find_source_by_id(source):
                    GLib.source_remove(source)
                if GLib.MainContext.default().find_source_by_id(expiry):
                    GLib.source_remove(expiry)
                window.destroy()


if __name__ == '__main__':
    unittest.main()
