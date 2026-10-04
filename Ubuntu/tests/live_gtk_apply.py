#!/usr/bin/env python3
"""Owned GTK action driver for the independently supervised live QA sequence.

Exercises the production dropdown and clicked handler, not global native input.
The supervisor owns baseline capture, independent identity checks and restoration.
"""
import argparse
from collections import Counter
import json
import os
from pathlib import Path
import sys
import time
from types import SimpleNamespace

import gi
gi.require_version("Graphene", "1.0")
gi.require_version("GdkPixbuf", "2.0")
from gi.repository import GLib, Gio, Graphene, GdkPixbuf
from codex_syncbar.app import SyncBarApplication, Gtk, Gsk
from codex_syncbar.ipc import Client


def capture_window(window, destination, profile_id, diagnostics=None):
    """Render the real owned GTK widget tree, independent of compositor frames.

    WidgetPaintable may have no cached node after Wayland occlusion. In that
    case snapshot the window's direct child through GTK's own snapshot API.
    Unmapped windows still wait for remapping; no pixels are synthesized.
    """
    diagnostics = diagnostics if diagnostics is not None else {}
    diagnostics['attempts'] = diagnostics.get('attempts', 0) + 1
    width, height = window.get_width(), window.get_height()
    diagnostics.update(width=width, height=height, mapped=window.get_mapped(),
                       visible=window.get_visible(), realized=window.get_realized(),
                       source='pending')
    if not window.get_mapped() or width <= 0 or height <= 0:
        return None
    paintable = getattr(window, '_qa_paintable', None)
    if paintable is None:
        paintable = Gtk.WidgetPaintable.new(window)
    snapshot = Gtk.Snapshot()
    paintable.snapshot(snapshot, float(width), float(height))
    node = snapshot.to_node()
    diagnostics['paintableNode'] = node is not None
    if node is not None:
        diagnostics['source'] = 'Gtk.WidgetPaintable'
    else:
        child = window.get_child()
        diagnostics['childMapped'] = child is not None and child.get_mapped()
        if child is not None and child.get_mapped():
            child_snapshot = Gtk.Snapshot()
            window.snapshot_child(child, child_snapshot)
            child_node = child_snapshot.to_node()
            diagnostics['childNode'] = child_node is not None
            if child_node is not None:
                snapshot = Gtk.Snapshot()
                snapshot.render_background(window.get_style_context(), 0, 0, width, height)
                snapshot.append_node(child_node)
                node = snapshot.to_node()
                diagnostics['source'] = 'Gtk.Widget.snapshot_child'
    diagnostics['renderNode'] = node is not None
    if node is None:
        return None
    destination = Path(destination)
    destination.mkdir(mode=0o700, parents=True, exist_ok=True)
    renderer = Gsk.CairoRenderer.new()
    renderer.realize_for_display(window.get_display())
    try:
        viewport = Graphene.Rect()
        viewport.init(0, 0, width, height)
        texture = renderer.render_texture(node, viewport)
        path = destination / f'live-profile-{profile_id}-{time.time_ns()}.png'
        if not texture.save_to_png(str(path)):
            raise RuntimeError('gtk_capture_write_failed')
        os.chmod(path, 0o600)
        pixels = GdkPixbuf.Pixbuf.new_from_file(str(path))
        data, stride, channels = pixels.get_pixels(), pixels.get_rowstride(), pixels.get_n_channels()
        samples = [tuple(data[y * stride + x * channels:y * stride + x * channels + 3])
                   for y in range(20, height - 20, 4) for x in range(20, width - 20, 4)]
        colors = Counter(samples)
        diagnostics['contentVisible'] = bool(samples) and len(colors) > 16 and colors.most_common(1)[0][1] < len(samples) * .995
        if not diagnostics['contentVisible']:
            path.unlink()
            return None
        return str(path)
    finally:
        renderer.unrealize()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--socket', required=True)
    parser.add_argument('--profile-id', type=int, required=True)
    parser.add_argument('--expected-targets', default='[]')
    parser.add_argument('--capture-only', action='store_true', help='Read-only selection and capture; never emits the apply signal')
    parser.add_argument('--capture-stress', action='store_true', help='Read-only capture after own-window hide/remap and occlusion by an owned test window')
    parser.add_argument('--timeout', type=int, default=300)
    args = parser.parse_args()
    if args.capture_stress and not args.capture_only:
        parser.error('--capture-stress requires --capture-only.')
    # A desktop restart must not kill the process responsible for restoring auth.
    if not args.capture_only and not any('codex-syncbar-live-qa-' in line and '.service' in line for line in Path('/proc/self/cgroup').read_text().splitlines()):
        parser.error('Run only as a child of the dedicated live QA supervisor user service.')
    targets = set(json.loads(args.expected_targets))
    if not args.capture_only and not targets:
        parser.error('--expected-targets is required when applying.')
    client = Client(args.socket)
    first = client.call('snapshot')
    client.close()
    if first.get('isDemo') or first.get('isBusy') or first.get('error'):
        raise SystemExit('Production service is not ready for supervised GTK QA.')
    options = SimpleNamespace(socket=args.socket, demo=False, capture=None, width=1040, height=760,
                              light=False, profile_id=None)
    app = SyncBarApplication(options)
    app.set_application_id('io.github.codexsyncbar.Ubuntu.LiveQA')
    app.set_flags(Gio.ApplicationFlags.NON_UNIQUE)
    state = {'step':0, 'deadline':time.monotonic()+args.timeout, 'ok':False, 'captureState':{}}

    def finish(error=None, operation=None, capture=None):
        state['ok'] = error is None
        result = {'ok':error is None, 'readOnlyCapture':args.capture_only, 'kind':'owned GTK widget capture' if args.capture_only else 'owned GTK dropdown and apply signal; not native pointer input'}
        if operation:
            result['usageDisplay'] = state.get('usageDisplay')
            result['operation'] = {k:operation.get(k) for k in ['id','kind','profileId','state','targets','isComplete']}
        result['driverState'] = {'step': state['step'], 'capture': state['captureState']}
        if error:
            result['error'] = error
        if capture:
            result['capture'] = capture
        if state.get('cover'):
            state['cover'].destroy()
        print(json.dumps(result, ensure_ascii=False), flush=True)
        app.quit()
        return False

    def capture_settled_frame():
        destination = Path(os.environ.get('SYNCBAR_LIVE_QA_CAPTURE_DIR', '/tmp/codex-syncbar-live-qa-captures'))
        return capture_window(app.window, destination, args.profile_id, state['captureState'])

    def tick():
        try:
            if state['step'] != 3 and time.monotonic() >= state['deadline']:
                return finish('gtk_apply_timeout')
            if state['step'] == 0 and app.accounts:
                index = next((i for i,a in enumerate(app.accounts) if a['id']==args.profile_id and not a.get('isPending') and not a.get('needsLogin')), None)
                if index is None:
                    return finish('profile_unavailable')
                app.account_choice.set_selected(index)
                state['step'] = 1
            elif state['step'] == 1 and app.preview and app.preview['profileId'] == args.profile_id:
                if args.capture_only:
                    app.window.present()
                    app.window.queue_draw()
                    state['captureAfter'] = time.monotonic() + 0.8
                    state['captureDeadline'] = time.monotonic() + 25
                    state['step'] = 3
                    if args.capture_stress:
                        app.window.set_visible(False)
                        def remap():
                            app.window.present()
                            def occlude():
                                cover = Gtk.Window(title='SyncBar read-only capture occlusion fixture')
                                cover.set_default_size(1200, 900)
                                cover.set_child(Gtk.Label(label='Owned GTK capture verification'))
                                cover.present()
                                state['cover'] = cover
                                return False
                            GLib.timeout_add(300, occlude)
                            return False
                        GLib.timeout_add(300, remap)
                        state['captureAfter'] = time.monotonic() + 1.5
                    return True
                included = {d['id'] for d in app.preview['targets'] if d['included']}
                if included != targets or not app.preview['canApply'] or not app.apply_button.get_sensitive():
                    return finish('preview_not_safe')
                state['previousOperationId'] = (app.snapshot.get('operation') or {}).get('id')
                app.apply_button.emit('clicked')
                if app.apply_button.get_sensitive():
                    return finish('duplicate_apply_not_disabled')
                state['step'] = 2
            elif state['step'] == 2:
                operation = app.snapshot.get('operation') or {}
                if operation.get('id') != state.get('previousOperationId') and operation.get('kind')=='switch' and operation.get('profileId')==args.profile_id and operation.get('isComplete') and not app.busy:
                    if operation['state']!='completed':
                        return finish('apply_did_not_complete', operation)
                    if app.snapshot.get('activeProfileId')!=args.profile_id:
                        return finish('local_display_mismatch', operation)
                    live_devices = {d['id']:d for d in app.snapshot.get('devices',[])}
                    if any(live_devices.get(t,{}).get('currentProfileId')!=args.profile_id for t in targets):
                        return finish('device_display_mismatch', operation)
                    usage = (app.selected() or {}).get('usage')
                    if not usage or usage.get('error') or not usage.get('updatedAt'):
                        return finish('usage_not_verified', operation)
                    displays = {}
                    for key, widget in [('session',app.session_quota),('weekly',app.weekly_quota)]:
                        quota = usage.get(key)
                        if widget.get_visible() != bool(quota):
                            return finish('missing_usage_display_mismatch', operation)
                        if quota:
                            remaining = quota.get('remainingPercent')
                            if remaining is None and quota.get('usedPercent') is not None:
                                remaining = 100-quota['usedPercent']
                            expected_text = '확인 필요' if remaining is None else f'{max(0,min(100,remaining)):.0f}% 남음'
                            if widget.value.get_text() != expected_text:
                                return finish('usage_display_mismatch', operation)
                            displays[key] = {'text':expected_text,'resetsAt':quota.get('resetsAt')}
                        else:
                            displays[key] = None
                    state['usageDisplay'] = {'updatedAt':usage['updatedAt'],'windows':displays}
                    state['completedOperation'] = operation
                    # A newly restarted desktop may occlude our surface and pause
                    # Wayland frame delivery. Remap this owned window so GTK
                    # lays out the final state before accepting any screenshot.
                    app.window.set_visible(False)
                    def show_final_state():
                        app.window.present()
                        app.window.queue_draw()
                        return False
                    GLib.timeout_add(200, show_final_state)
                    state['captureAfter'] = time.monotonic() + 1.2
                    state['captureDeadline'] = time.monotonic() + 25
                    state['step'] = 3
            elif state['step'] == 3 and time.monotonic() >= state['captureAfter']:
                capture = capture_settled_frame()
                if capture:
                    return finish(operation=state.get('completedOperation'), capture=capture)
                if time.monotonic() >= state['captureDeadline']:
                    return finish('gtk_capture_unavailable', state.get('completedOperation'))
                app.window.queue_draw()
            return True
        except Exception as error:
            state['captureState']['exceptionType'] = type(error).__name__
            code = str(error) if str(error) == 'gtk_capture_write_failed' else 'gtk_driver_failed'
            return finish(code, state.get('completedOperation'))
    def activated(*_):
        app.window._qa_paintable = Gtk.WidgetPaintable.new(app.window)
        GLib.timeout_add(200, tick)
    app.connect_after('activate', activated)
    app.run([])
    return 0 if state['ok'] else 1


if __name__=='__main__':
    raise SystemExit(main())
