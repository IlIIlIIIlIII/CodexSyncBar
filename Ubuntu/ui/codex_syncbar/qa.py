"""In-process GTK integration checks against an explicitly synthetic daemon.

This does not drive the user's desktop or claim pointer/keyboard E2E coverage.
"""
import argparse
import json
from pathlib import Path
from types import SimpleNamespace
import time

from gi.repository import GLib
from .app import SyncBarApplication
from .dialogs import ManagerDialog
from .ipc import Client


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--socket', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--width', type=int, default=1040)
    parser.add_argument('--height', type=int, default=760)
    args = parser.parse_args()
    client = Client(args.socket)
    first = client.call('snapshot')
    if first.get('isDemo') is not True:
        raise SystemExit('This harness refuses non-demo services.')
    client.close()
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    options = SimpleNamespace(socket=args.socket, demo=True, capture=None, width=args.width, height=args.height, light=False, profile_id=2, page='usage')
    app = SyncBarApplication(options)
    report = {'kind':'in-process GTK + real demo IPC integration; not native pointer/keyboard E2E', 'checks':[]}
    state = {'step':0,'deadline':time.monotonic()+20}
    def record(name):
        report['checks'].append({'name':name,'passed':True})
    def finish(error=None):
        report['passed'] = error is None
        if error:
            report['error'] = str(error)
        (output/f'gtk-integration-{args.width}x{args.height}.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
        print(json.dumps(report, ensure_ascii=False), flush=True)
        app.quit()
        return False
    def tick():
        try:
            if time.monotonic() > state['deadline']:
                raise TimeoutError(f'GTK integration step {state["step"]} timed out')
            step = state['step']
            if step == 0 and app.preview:
                assert app.snapshot['activeProfileId'] == 1
                assert app.selected_id == 2
                assert all(t['currentProfileId'] == 1 for t in app.preview['targets'] if t['included'])
                assert app.pages.get_visible_child_name() == 'usage'
                assert len(app.overview.rows) == len(app.accounts)
                overview_row = app.overview.rows[2]
                assert overview_row.grab_focus()
                app.render_overview()
                assert app.overview.rows[2] is overview_row
                record('weekly overview opens by default and preserves rows and keyboard focus across polling')
                app.overview.list.emit('row-activated', overview_row)
                assert app.pages.get_visible_child_name() == 'switch'
                record('selecting an account leaves every active account unchanged')
                assert app.weekly_quota.get_visible()
                assert app.weekly_quota.value.get_text() == '58% 남음'
                quota_widgets = []
                child = app.quotas.get_first_child()
                while child is not None:
                    quota_widgets.append(child)
                    child = child.get_next_sibling()
                assert quota_widgets == [app.weekly_quota]
                assert 'session' not in app.selected()['usage']
                assert 'fiveHour' not in app.snapshot['settings']
                record('only the weekly quota reaches the IPC response and rendered account details')
                # Construct the production dialogs and their actual widgets, not a second demo UI.
                for name in ['accounts','devices','cli','tokens','settings']:
                    dialog = ManagerDialog(app, name)
                    assert dialog.get_child() is not None
                    if name == 'devices':
                        dialog.edit_device({'id':'dev','displayName':'개발 서버','host':'dev.example.invalid','username':'demo','port':22,'enabled':True})
                    dialog.closed = True
                record('all five production management dialogs and SSH form construct')
                actual_operation = app.snapshot.get('operation')
                app.snapshot['operation'] = {'kind':'login', 'profileId':2, 'state':'running', 'message':'계정 전용 Chrome 창에서 로그인하세요…'}
                app.busy = True
                app.render_state()
                assert app.apply_button.get_label() == '로그인 대기 중'
                assert app.login_actions.get_visible() and not app.spinner.get_visible()
                assert not app.apply_button.get_sensitive()
                app.snapshot['operation'] = actual_operation
                app.busy = False
                app.render_state()
                record('login waiting exposes reopen and cancel without an applying spinner')
                # Rapid changes must discard the earlier preview response.
                assert app.account_choice.grab_focus()
                record('account picker accepts keyboard focus')
                app.account_choice.set_selected(0)
                app.account_choice.set_selected(1)
                state['step'] = 1
            elif step == 1 and app.preview and app.preview['profileId'] == 2:
                assert app.apply_button.get_sensitive()
                record('rapid selection leaves preview bound to the latest account')
                app.apply_button.emit('clicked')
                assert not app.apply_button.get_sensitive()
                record('apply immediately disables duplicate activation')
                state['step'] = 2
            elif step == 2 and app.snapshot.get('activeProfileId') == 2 and not app.busy:
                assert app.snapshot['operation']['state'] == 'completed'
                record('GTK apply signal crosses IPC and completes A to B')
                app.account_choice.set_selected(0)
                state['step'] = 3
            elif step == 3 and app.preview and app.preview['profileId'] == 1:
                app.apply_button.emit('clicked')
                state['step'] = 4
            elif step == 4 and app.snapshot.get('activeProfileId') == 1 and not app.busy:
                assert app.snapshot['operation']['state'] == 'completed'
                record('GTK apply signal crosses IPC and completes B to A')
                adjustment = app.scroll.get_vadjustment()
                adjustment.set_value(adjustment.get_upper()-adjustment.get_page_size())
                last_row = app.device_list.get_last_child()
                assert last_row.grab_focus()
                record('last device row accepts focus and the body scrolls to its end')
                app.pages.set_visible_child_name('usage')
                app.render_overview()
                assert app.overview.rows[1].active.get_visible()
                assert not app.overview.rows[2].active.get_visible()
                app.overview.switch.emit('clicked')
                assert app.pages.get_visible_child_name() == 'switch'
                app.pages.set_visible_child_name('usage')
                app.overview.refresh.emit('clicked')
                assert not app.overview.refresh.get_sensitive()
                record('overview reflects active-account changes, footer navigates, and refresh disables duplicate clicks')
                state['step'] = 5
            elif step == 5 and not app.busy and not app._refresh_requested and app.overview.refresh.get_sensitive():
                assert app.snapshot['activeProfileId'] == 1
                record('overview refresh completes through demo IPC without switching accounts')
                return finish()
            return True
        except Exception as error:
            return finish(error)
    app.connect('activate', lambda *_: GLib.timeout_add(150, tick))
    code = app.run([])
    return code if report.get('passed') else 1


if __name__ == '__main__':
    raise SystemExit(main())
