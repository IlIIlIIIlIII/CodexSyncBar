"""Native management dialogs backed by service commands, never placeholder actions."""
from gi.repository import Adw, GLib, Gtk
from .app import account_name, box, button, clear, label


class ManagerDialog(Adw.Dialog):
    def __init__(self, app, page, device_id=None):
        super().__init__()
        self.app, self.page, self.device_id = app, page, device_id
        self.set_content_width(720)
        self.set_content_height(640)
        self.set_title({'accounts':'계정 관리','devices':'장치 관리','cli':'Codex CLI 관리','tokens':'토큰 집계','settings':'설정'}[page])
        view = Adw.ToolbarView()
        header = Adw.HeaderBar()
        header.set_title_widget(Adw.WindowTitle(title=self.get_title()))
        view.add_top_bar(header)
        self.set_child(view)
        outer = box(spacing=0)
        view.set_content(outer)
        scroll = Gtk.ScrolledWindow(vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER)
        outer.append(scroll)
        self.body = box(spacing=18)
        self.body.add_css_class('dialog-body')
        scroll.set_child(self.body)
        self.feedback = label('', 'small', True)
        self.feedback.set_margin_start(20)
        self.feedback.set_margin_end(20)
        self.feedback.set_margin_bottom(14)
        outer.append(self.feedback)
        self.controls = []
        self.job_id = None
        self.job_polling = False
        self.closed = False
        self.connect('closed', lambda *_: setattr(self, 'closed', True))
        self.render()
        GLib.timeout_add(800, self.poll_job)

    def render(self):
        clear(self.body)
        getattr(self, f'build_{self.page}')()

    def run(self, method, params=None, callback=None):
        self.feedback.set_text('처리 중…')
        self.body.set_sensitive(False)
        def complete(result, error):
            self.body.set_sensitive(True)
            self.feedback.set_text(str(error) if error else '요청을 처리했습니다.')
            if result and isinstance(result, dict) and result.get('message'):
                self.feedback.set_text(result['message'])
            if not error and isinstance(result, dict) and result.get('id') and result.get('state'):
                self.job_id = result['id']
            if callback:
                callback(result, error)
            self.app.poll()
            return False
        self.app.client.submit(method, params, complete, timeout=120)

    def poll_job(self):
        if self.closed:
            return False
        if not self.job_id or self.job_polling:
            return True
        self.job_polling = True
        def got(result, error):
            self.job_polling = False
            if error:
                self.feedback.set_text(str(error))
            elif result:
                self.feedback.set_text(result.get('message', '처리 중…'))
                if result.get('isComplete') or result.get('state') in ('completed', 'failed', 'cancelled', 'recoveryRequired'):
                    self.job_id = None
                    self.app.poll()
                    payload = result.get('result')
                    if self.page == 'cli' and isinstance(payload, list):
                        group = self.group('업데이트 결과')
                        for item in payload:
                            row = Adw.ActionRow(title=item.get('deviceId', '장치'), subtitle=item.get('message') or item.get('state', '확인 필요'))
                            group.add(row)
                    elif self.page == 'accounts':
                        self.reload_accounts(None, None)
            return False
        self.app.client.submit('operation', {'operationId':self.job_id}, got)
        return True

    def confirm(self, heading, body, action, callback, destructive=False):
        dialog = Adw.AlertDialog(heading=heading, body=body)
        dialog.add_response('cancel', '취소')
        dialog.add_response('confirm', action)
        dialog.set_default_response('cancel')
        dialog.set_close_response('cancel')
        if destructive:
            dialog.set_response_appearance('confirm', Adw.ResponseAppearance.DESTRUCTIVE)
        else:
            dialog.set_response_appearance('confirm', Adw.ResponseAppearance.SUGGESTED)
        dialog.connect('response', lambda _dialog, response: callback() if response == 'confirm' else None)
        dialog.present(self.app.window)

    def group(self, title, description=None):
        group = Adw.PreferencesGroup(title=title, description=description or '')
        self.body.append(group)
        return group

    def build_accounts(self):
        actions = box(False)
        actions.append(button('계정 추가', callback=lambda *_: self.run('account.add', callback=self.reload_accounts), css='suggested-action'))
        actions.append(button('인증 파일 가져오기', callback=self.import_auth))
        actions.append(button('목록 새로고침', callback=lambda *_: self.reload_accounts(None, None)))
        self.body.append(actions)
        self.account_group = self.group('등록된 계정', '계정을 선택해도 실제 적용 계정은 바뀌지 않습니다.')
        for account in self.app.accounts:
            profile_id = account['id']
            group = Adw.ExpanderRow(title=account_name(account), subtitle=account.get('email', ''))
            self.account_group.add(group)
            alias = Adw.EntryRow(title='별칭 (최대 5자)')
            alias.set_text(account.get('alias') or '')
            group.add_row(alias)
            controls = box(False, 6)
            controls.set_margin_top(8)
            controls.set_margin_bottom(8)
            for text, method, params in [
                ('별칭 저장','account.rename',None), ('위로','account.move',{'profileId':profile_id,'offset':-1}),
                ('아래로','account.move',{'profileId':profile_id,'offset':1}), ('로그인','account.login',{'profileId':profile_id}),
                ('인증 갱신','auth.refresh',{'profileId':profile_id})]:
                def clicked(_button, m=method, p=params, a=alias, pid=profile_id):
                    self.run(m, p if p is not None else {'profileId':pid,'alias':a.get_text()}, self.reload_accounts)
                controls.append(button(text, callback=clicked))
            group.add_row(controls)
            secondary = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, column_spacing=6, row_spacing=6, max_children_per_line=3)
            secondary.set_margin_bottom(8)
            secondary.append(button('로그인 창 다시 열기', callback=lambda _b,p=profile_id: self.run('account.reopen', {'profileId':p})))
            secondary.append(button('로그인 취소', callback=lambda _b,p=profile_id: self.run('account.cancel', {'profileId':p})))
            secondary.append(button('인증 동기화', callback=lambda _b,p=profile_id: self.run('auth.sync', {'profileId':p})))
            secondary.append(button('새 브라우저로 로그인', callback=lambda _b,p=profile_id: self.confirm('브라우저 로그인 상태를 초기화할까요?', '이 계정의 전용 브라우저 프로필을 초기화한 뒤 다시 로그인합니다.', '새로 로그인', lambda: self.run('account.login', {'profileId':p,'fresh':True}, self.reload_accounts))))
            secondary.append(button('다른 계정으로 인증 교체', callback=lambda _b,p=profile_id: self.confirm('보관 인증을 교체할까요?', '로그인하는 계정으로 이 항목의 인증을 교체합니다. 기존 인증은 로그인 성공 전까지 보존합니다.', '로그인 시작', lambda: self.run('account.login', {'profileId':p,'fresh':True,'replaceExisting':True}, self.reload_accounts))))
            group.add_row(secondary)
            fallback = [a for a in self.app.accounts if a['id'] != profile_id and not a.get('needsLogin') and not a.get('isPending')]
            logout_row = box(False, 8)
            logout_row.set_margin_bottom(10)
            if fallback:
                chooser = Gtk.DropDown.new_from_strings([account_name(a) for a in fallback])
                chooser.set_hexpand(True)
                chooser.set_tooltip_text('로그아웃할 계정을 대신 적용할 계정')
                logout_row.append(chooser)
                logout_row.append(button('로그아웃', callback=lambda _b,p=profile_id,c=chooser,items=fallback: self.logout(p,items[c.get_selected()]['id'])))
            else:
                logout_row.append(label('로그아웃하려면 다른 로그인 계정이 필요합니다.', 'muted small', True))
            if account.get('isPending') or account.get('needsLogin'):
                logout_row.append(button('계정 삭제', callback=lambda _b,p=profile_id: self.confirm('계정을 삭제할까요?', '선택한 계정의 보관 정보가 제거됩니다.', '삭제', lambda: self.run('account.delete', {'profileId':p}, self.reload_accounts), True)))
            group.add_row(logout_row)

    def reload_accounts(self, result, error):
        if error:
            return
        def loaded(snapshot, failure):
            if not failure:
                self.app.on_snapshot(snapshot, None)
                self.render()
            return False
        self.app.client.submit('snapshot', callback=loaded)

    def logout(self, profile_id, fallback):
        name = account_name(self.app.lookup(fallback))
        self.confirm('이 계정에서 로그아웃할까요?', f'이 계정을 사용하는 장치에는 {name}을 적용한 뒤 보관 인증을 제거합니다.', '로그아웃', lambda: self.run('account.logout', {'profileId':profile_id,'fallbackProfileId':fallback}, self.reload_accounts))

    def import_auth(self, *_):
        chooser = Gtk.FileDialog(title='Codex 인증 파일 가져오기')
        def selected(dialog, result):
            try:
                file = dialog.open_finish(result)
                if file:
                    self.run('account.import', {'path':file.get_path()}, self.reload_accounts)
            except Exception:
                pass  # File chooser cancellation is not a failed import.
        chooser.open(self.app.window, None, selected)

    def build_devices(self):
        top = box(False)
        top.append(button('SSH 장치 추가', callback=lambda *_: self.edit_device({}), css='suggested-action'))
        top.append(button('목록 새로고침', callback=lambda *_: self.render()))
        self.body.append(top)
        self.devices_group = self.group('SSH 장치', '설치 및 활성화를 완료한 장치가 전체 계정 전환에 포함됩니다.')
        def loaded(result, error):
            if error:
                self.feedback.set_text(str(error))
                return False
            devices = result.get('devices', []) if isinstance(result, dict) else result or []
            for device in devices:
                row = Adw.ActionRow(title=device.get('displayName') or device.get('host', 'SSH'), subtitle=f"{device.get('username','')}@{device.get('host','')}:{device.get('port',22)}")
                row.add_suffix(button('관리', callback=lambda _b,d=device: self.edit_device(d)))
                self.devices_group.add(row)
                if self.device_id in (device.get('id'), 'ssh:' + device.get('id','')):
                    self.device_id = None
                    self.edit_device(device)
            if not devices:
                self.devices_group.add(Adw.ActionRow(title='등록된 SSH 장치가 없습니다.'))
            return False
        self.app.client.submit('device.list', callback=loaded)

    def edit_device(self, device):
        clear(self.body)
        self.body.append(button('장치 목록으로', callback=lambda *_: self.render()))
        group = self.group('SSH 연결')
        fields = {}
        for key, title, default in [('displayName','표시 이름',''),('host','호스트',''),('port','포트',22),('username','사용자 이름','')]:
            entry = Adw.EntryRow(title=title)
            entry.set_text(str(device.get(key, default)))
            fields[key] = entry
            group.add(entry)
        auth_values = ['openSSHConfig','privateKey','password']
        auth_labels = ['OpenSSH 설정 사용','개인 키','비밀번호']
        auth = Adw.ComboRow(title='인증 방식', model=Gtk.StringList.new(auth_labels))
        auth.set_selected(auth_values.index(device.get('authentication')) if device.get('authentication') in auth_values else 0)
        group.add(auth)
        identity = Adw.EntryRow(title='개인 키 파일')
        identity.set_text(device.get('identityFile') or '')
        group.add(identity)
        certificate = Adw.EntryRow(title='SSH 인증서 (선택)')
        certificate.set_text(device.get('certificateFile') or '')
        group.add(certificate)
        password = Adw.PasswordEntryRow(title='새 비밀번호 (빈칸은 기존 값 유지)')
        group.add(password)
        passphrase = Adw.PasswordEntryRow(title='새 키 암호 (빈칸은 기존 값 유지)')
        group.add(passphrase)
        clear_passphrase = Adw.SwitchRow(title='저장된 키 암호 지우기', subtitle='암호가 없는 키로 변경할 때 사용합니다.')
        group.add(clear_passphrase)
        def mode_changed(*_):
            value = auth_values[auth.get_selected()]
            identity.set_visible(value == 'privateKey')
            certificate.set_visible(value == 'privateKey')
            passphrase.set_visible(value == 'privateKey')
            clear_passphrase.set_visible(value == 'privateKey' and bool(device.get('hasKeyPassphrase')))
            password.set_visible(value == 'password')
        auth.connect('notify::selected', mode_changed)
        mode_changed()
        enabled = Adw.SwitchRow(title='전체 적용에 포함', subtitle='새 장치는 설치 및 활성화 후 포함할 수 있습니다.')
        enabled.set_active(bool(device.get('enabled')))
        enabled.set_sensitive(bool(device.get('enabled')))
        group.add(enabled)
        def save(*_):
            try:
                port = int(fields['port'].get_text())
                if not 1 <= port <= 65535:
                    raise ValueError()
            except ValueError:
                self.feedback.set_text('포트는 1~65535 사이의 숫자여야 합니다.')
                return
            draft = {key:entry.get_text().strip() for key,entry in fields.items()}
            draft.update({'port':port,'authentication':auth_values[auth.get_selected()],'identityFile':identity.get_text().strip() or None,'certificateFile':certificate.get_text().strip() or None,'enabled':enabled.get_active()})
            if device.get('id'):
                draft['id'] = device['id']
            params = {'device':draft,'clearPassphrase':clear_passphrase.get_active()}
            if password.get_text():
                params['password'] = password.get_text()
            if passphrase.get_text():
                params['passphrase'] = passphrase.get_text()
            self.run('device.save', params, lambda result,error: self.render() if not error else None)
            password.set_text('')
            passphrase.set_text('')
        self.body.append(button('저장', callback=save, css='suggested-action'))
        if not device.get('id'):
            return
        device_id = device['id']
        tools = box(False, 8)
        tools.append(button('연결 테스트', callback=lambda *_: self.run('device.test', {'deviceId':device_id})))
        tools.append(button('호스트 키 확인', callback=lambda *_: self.check_trust(device_id)))
        tools.append(button('설치 및 활성화', callback=lambda *_: self.check_trust(device_id, bootstrap=True)))
        self.body.append(tools)
        if self.app.selected_id is not None:
            self.body.append(button('이 장치에 선택한 계정 적용', callback=lambda *_: self.apply_device(device_id)))
        self.body.append(button('장치 삭제', callback=lambda *_: self.confirm('SSH 장치를 삭제할까요?', '목록에서 제거하며 원격 장치의 계정은 변경하지 않습니다.', '삭제', lambda: self.run('device.delete', {'deviceId':device_id}, lambda r,e: self.render() if not e else None), True)))

    def check_trust(self, device_id, bootstrap=False):
        def inspected(result, error):
            if error:
                return
            if result.get('trusted'):
                if bootstrap:
                    self.run('device.bootstrap', {'deviceId':device_id})
                else:
                    self.feedback.set_text('등록된 호스트 키와 일치합니다.')
                return
            key = result.get('hostKey')
            if not key:
                self.feedback.set_text('호스트 키를 확인하지 못했습니다.')
                return
            def approve():
                self.run('device.trust', {'deviceId':device_id,'approvedKey':key}, lambda r,e: self.run('device.bootstrap', {'deviceId':device_id}) if bootstrap and not e else None)
            self.confirm('이 서버의 호스트 키를 신뢰할까요?', f"{key.get('host')}:{key.get('port')}\n\n{key.get('fingerprint')}\n\n서버 관리자가 제공한 지문과 비교해 주세요.", '신뢰하고 등록', approve)
        self.run('device.trust', {'deviceId':device_id}, inspected)

    def apply_device(self, device_id):
        def ready(preview, error):
            if error:
                return
            if not preview.get('canApply'):
                self.feedback.set_text(preview.get('blockingReason') or '적용할 수 없습니다.')
                return
            import uuid
            self.confirm('이 SSH 장치에 적용할까요?', f'{account_name(self.app.selected())}을 이 장치에만 적용합니다.', '적용', lambda: self.run('apply', {'previewId':preview['previewId'],'requestId':uuid.uuid4().hex}))
        self.run('preview', {'profileId':self.app.selected_id,'deviceId':device_id}, ready)

    def build_cli(self):
        self.body.append(label('설치된 Codex CLI의 버전과 업데이트 결과를 확인합니다.', 'muted', True))
        self.body.append(button('모든 기기 CLI 업데이트', callback=lambda *_: self.confirm('모든 기기의 CLI를 업데이트할까요?', '활성 장치의 Codex CLI를 업데이트하고 관리 대상 연결을 다시 연결합니다.', '업데이트', lambda: self.run('cli.updateAll')), css='suggested-action'))
        group = self.group('장치별 CLI')
        devices = self.app.snapshot.get('devices') or [{'id':'local','displayName':'이 Ubuntu PC'}]
        for device in devices:
            device_id = device.get('id', 'local')
            row = Adw.ActionRow(title=device.get('displayName','장치'), subtitle='버전을 확인해 주세요.')
            def fetch(_b, did=device_id, r=row):
                def got(result, error):
                    if not error:
                        r.set_subtitle(cli_description(result))
                self.run('cli.status', {'deviceId':did}, got)
            row.add_suffix(button('조회', callback=fetch))
            row.add_suffix(button('업데이트', callback=lambda _b,did=device_id: self.run('cli.update', {'deviceId':did})))
            group.add(row)

    def build_tokens(self):
        self.body.append(label('Codex 세션 기록에서 집계한 사용량입니다. 청구 금액과 다를 수 있습니다.', 'muted', True))
        self.body.append(button('다시 집계', callback=lambda *_: self.render()))
        group = self.group('토큰 사용량')
        def loaded(result, error):
            if error:
                self.feedback.set_text(str(error))
                return False
            if not result:
                group.add(Adw.ActionRow(title='집계된 기록이 없습니다.'))
                return False
            devices = result.get('devices', []) if isinstance(result, dict) else []
            if devices:
                for device in devices:
                    row = Adw.ExpanderRow(title=device.get('displayName', '장치'), subtitle=device.get('error') or ('집계 완료' if device.get('isReachable') else '연결 확인 필요'))
                    for title, value in token_rows(device.get('summary') or {}):
                        detail = Adw.ActionRow(title=title)
                        detail.add_suffix(label(value))
                        row.add_row(detail)
                    if isinstance(device.get('estimatedCostUsd'), (int,float)):
                        cost = Adw.ActionRow(title='추정 비용 (USD)')
                        cost.add_suffix(label(f"${device['estimatedCostUsd']:,.2f}"))
                        row.add_row(cost)
                    for bucket in (device.get('summary') or {}).get('buckets', []):
                        model = Adw.ActionRow(title=bucket.get('model', '모델 확인 필요'), subtitle=bucket.get('serviceTier', 'default'))
                        model.add_suffix(label(f"{bucket.get('totalTokens',0):,} 토큰"))
                        row.add_row(model)
                    group.add(row)
                return False
            for title, value in token_rows(result):
                row = Adw.ActionRow(title=title)
                row.add_suffix(label(value))
                group.add(row)
            return False
        self.app.client.submit('tokens', callback=loaded, timeout=120)

    def build_settings(self):
        settings = self.app.snapshot.get('settings') or {}
        group = self.group('표시와 실행')
        switches = {}
        for key,title,subtitle,default in [('fiveHour','5시간 사용량 표시','계정에서 제공하는 경우 표시합니다.',True),('codexWeekly','주간 사용량 표시','선택한 계정의 주간 잔여량을 표시합니다.',True),('launchAtLogin','로그인 시 자동 시작','창을 열지 않고 백그라운드 서비스를 시작합니다.',False)]:
            row = Adw.SwitchRow(title=title, subtitle=subtitle)
            row.set_active(settings.get(key, default))
            group.add(row)
            switches[key] = row
        self.body.append(button('설정 저장', callback=lambda *_: self.run('settings.save', {**settings, **{key:row.get_active() for key,row in switches.items()}}), css='suggested-action'))
        weekly = self.group('주간 자동 메시지', '기본값은 꺼짐입니다. 활성화한 계정에서만 주간 시작 메시지를 보냅니다.')
        weekly_settings = settings.get('weekly') or {}
        enabled_ids = weekly_settings.get('preferences', {}).get('enabledProfileIds', [])
        for account in self.app.accounts:
            row = Adw.SwitchRow(title=account_name(account))
            row.set_active(account['id'] in enabled_ids)
            row.connect('notify::active', lambda widget,_prop,p=account['id']: self.run('weekly.set', {'profileId':p,'enabled':widget.get_active()}))
            weekly.add(row)
            send = Adw.ActionRow(title=account_name(account), subtitle='주간 시작 메시지를 지금 보냅니다.')
            send_button = button('지금 보내기', callback=lambda _b,p=account['id']: self.confirm('주간 시작 메시지를 보낼까요?', '선택한 계정으로 Codex 메시지를 한 번 전송합니다.', '보내기', lambda: self.run('weekly.send', {'profileId':p})))
            send_button.set_sensitive(not account.get('needsLogin') and not account.get('isPending'))
            send.add_suffix(send_button)
            weekly.add(send)
        self.body.append(label('창을 닫아도 백그라운드 갱신과 진행 중인 전환은 계속됩니다. 메뉴의 “완전히 종료”로 서비스를 종료할 수 있습니다.', 'muted small', True))


def cli_description(result):
    if not isinstance(result, dict):
        return str(result or '조회 결과 없음')
    parts = [result.get('version') or result.get('currentVersion'), result.get('path') or result.get('executablePath'), result.get('message') or result.get('detail') or result.get('notice')]
    return ' · '.join(str(value) for value in parts if value) or '버전 정보를 확인하지 못했습니다.'


def token_rows(result):
    if not isinstance(result, dict):
        return [('집계 결과', '확인 필요')]
    totals = result.get('totals') or result.get('total') or result
    if not isinstance(totals, dict):
        totals = result
    fields = [('입력 토큰','inputTokens'),('캐시 입력','cachedInputTokens'),('출력 토큰','outputTokens'),('추론 토큰','reasoningOutputTokens'),('전체 토큰','totalTokens')]
    rows = [(title, f'{totals[key]:,}') for title,key in fields if isinstance(totals.get(key), (int,float))]
    return rows or [('기록 상태', result.get('message') or '집계된 기록이 없습니다.')]
