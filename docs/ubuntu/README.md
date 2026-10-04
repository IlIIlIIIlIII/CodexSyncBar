# Ubuntu 앱

Codex SyncBar Ubuntu는 **GTK 4·libadwaita 화면과 .NET 10 사용자 서비스**로 구성된 네이티브 계정 관리자입니다. 현재 적용 계정과 적용할 계정을 나란히 보여 주고, 사용량 및 로컬·SSH 장치별 `현재 → 적용 예정` 상태를 확인한 뒤 전환합니다. 계정을 선택하는 것만으로 인증을 변경하지 않습니다.

등록된 계정의 이메일은 전체 주소로 표시하며, 계정별 별칭도 사용할 수 있습니다. 화면과 일반 IPC 응답에는 인증 토큰을 표시하지 않습니다. 첫 화면 예시는 데모 데이터입니다. 실계정 화면 기록은 로컬 QA 자료로 별도 보관합니다.

Ubuntu의 Yaru 색상 토큰, 시스템 강조색, 밝은/어두운 테마, 기본 GTK 컨트롤을 사용합니다. 넓은 창에서는 두 열로 표시하고 작은 창에서는 세로로 배치합니다. 최소 창 크기는 600×480입니다. Windows 위젯 보드·COM 제공자와 WSL 관리 기능은 Ubuntu 빌드에 포함되지 않습니다.

![Ubuntu 네이티브 화면 — 가상 계정을 사용하는 격리 데모](qa-evidence/native-default.png)

## 요구 환경

- 배포 대상: **Ubuntu 26.04 amd64**, 로그인한 데스크톱 사용자와 systemd 사용자 세션.
- GTK 4, libadwaita, Python 3/PyGObject, GNOME Secret Service. `.deb`는 필요한 Ubuntu 라이브러리 의존성을 선언합니다.
- bash, jq, tar, OpenSSH client, Node.js, xdg-utils.
- 실제 계정 로그인에는 Codex CLI와 Chrome 또는 Chromium이 필요합니다. CLI가 없는 경우 앱의 **Codex CLI 관리**에서 사용자용 설치를 진행할 수 있습니다.
- SSH 대상에는 bash·jq·tar 등 앱에서 확인하는 필수 도구가 필요합니다. Node.js가 없는 원격 장치는 지원되는 jq 사용량 집계 경로를 이용합니다. 실제 Codex 사용 및 실계정 검증에는 해당 장치의 Codex CLI도 필요합니다.
- 소스 빌드에는 **.NET 10 SDK**가 필요합니다. 배포본은 .NET 런타임을 포함하므로 실행할 때 SDK를 설치할 필요는 없습니다.

화면 및 로컬 실행 검증 환경은 Ubuntu 26.04.1, GNOME 50.1/Wayland, GTK 4.22.4, libadwaita 1.9.1입니다. 다른 배포판·아키텍처에 대한 설치 검증은 아직 하지 않았습니다.

## 빌드와 패키지

모든 명령은 저장소 루트에서 실행합니다. Ubuntu 개발 환경의 라이브러리가 없는 경우 다음과 같이 준비할 수 있습니다. .NET 10 SDK는 별도로 준비합니다.

```bash
sudo apt update
sudo apt install python3 python3-gi gir1.2-gtk-4.0 gir1.2-adw-1 \
  gir1.2-secret-1 nodejs jq bash tar openssh-client xdg-utils

bash Scripts/Ubuntu/test.sh
bash Scripts/Ubuntu/package.sh --no-build
```

`test.sh`는 공용 코어·Ubuntu 코어·셸 도우미·Python 계약 검증을 실행하고, 배포용 앱을 빌드한 뒤 격리된 데모 서비스의 IPC를 검증합니다. `package.sh --no-build`는 이미 검증한 빌드로 `.deb`를 만들고 압축을 풀어 구조를 검사합니다. 빌드만 필요한 경우에는 다음 명령을 사용합니다.

```bash
bash Scripts/Ubuntu/build.sh
bash Scripts/Ubuntu/package.sh --no-build
```

SDK나 Node.js가 PATH에 없으면 실행 파일을 지정할 수 있습니다.

```bash
SYNCBAR_DOTNET=/absolute/path/to/dotnet \
SYNCBAR_NODE=/absolute/path/to/node \
bash Scripts/Ubuntu/test.sh
```

출력은 `dist/ubuntu`입니다. 기본 파일은 `codex-syncbar_1.0.0.28+ubuntu1_amd64.deb`와 같은 이름의 `.sha256`이며, `SYNCBAR_VERSION`으로 패키지 버전을 지정할 수 있습니다. `SYNCBAR_OUTPUT`으로 출력 디렉터리를 바꿀 수 있습니다. 현재 `.deb`는 Ubuntu 26.04의 라이브러리 이름과 버전을 대상으로 합니다.

### 시스템 설치

```bash
cd dist/ubuntu
sha256sum --check codex-syncbar_1.0.0.28+ubuntu1_amd64.deb.sha256
sudo apt install ./codex-syncbar_1.0.0.28+ubuntu1_amd64.deb
systemctl --user daemon-reload
codex-syncbar
```

앱은 `/usr/lib/codex-syncbar`, 실행기는 `/usr/bin/codex-syncbar`에 설치됩니다. 설치 패키지에는 서비스를 시작하거나 로그인 자동 시작을 켜는 maintainer script가 없습니다. 실행기를 명시적으로 열 때 백엔드가 시작됩니다. 시스템 설치 명령은 안내이며, 이번 검증에서는 `.deb`를 시스템에 설치하지 않고 패키지 구조만 검사했습니다.

### 관리자 권한 없는 사용자 설치

빌드 후 다음 명령으로 현재 사용자에게 설치합니다. 시스템 GTK/Python/Secret Service 라이브러리는 이미 있어야 합니다.

```bash
bash Scripts/Ubuntu/install-user.sh
~/.local/bin/codex-syncbar
```

시스템에 Node.js가 없고 사용 가능한 Node.js 실행 파일이 있다면 설치본에 복사할 수 있습니다.

```bash
bash Scripts/Ubuntu/install-user.sh --node /absolute/path/to/node
```

이 옵션은 기존 실행 파일을 복사하며 인터넷에서 Node.js를 내려받지 않습니다. 사용자 설치 위치는 다음과 같습니다.

| 항목 | 위치 |
|---|---|
| 앱·.NET 런타임·선택적인 Node.js | `~/.local/share/codex-syncbar/app` |
| 실행기 | `~/.local/bin/codex-syncbar` |
| 앱 목록 항목 | `~/.local/share/applications/io.github.codexsyncbar.Ubuntu.desktop` |
| 사용자 서비스 | `~/.config/systemd/user/codex-syncbar.service` |

설치기는 심볼릭 링크나 자신이 관리하지 않는 기존 파일을 덮어쓰지 않습니다. 같은 설치기로 업그레이드하면 이전 앱 디렉터리를 `.app-previous-*`로 보존합니다. 업그레이드 전 진행 중인 작업을 끝내고 앱 메뉴의 **완전히 종료**로 백엔드를 종료하세요. 사용자 설치와 시스템 설치는 같은 서비스 이름을 사용하므로 하나의 설치 방식을 선택합니다.

이번 환경에서는 사용자 설치를 실제로 완료했고, 설치 직후 서비스가 `loaded / inactive / static`, `MainPID=0`인 것을 확인했습니다. `static`은 자동 시작용 `[Install]` 설정을 두지 않았다는 뜻입니다. 설치본의 핵심 파일 해시가 빌드와 일치했으며, 설치된 바이너리로 격리 IPC 검증도 통과했습니다.

## 사용 흐름

1. 앱 메뉴의 **계정 관리**에서 계정을 추가해 로그인하거나 기존 인증 파일을 가져옵니다. 로그인은 계정별 별도 Chrome/Chromium 프로필과 임시 Codex 홈을 사용합니다.
2. **장치 관리**에서 SSH 장치를 등록하고 연결을 확인합니다. 처음 연결하는 호스트는 지문을 확인하고 신뢰를 등록합니다. 설치·활성화를 마친 장치만 전체 적용 대상에 포함됩니다.
3. 기본 화면에서 적용할 계정을 선택합니다. 현재 계정, 남은 사용량, 초기화 시각, 장치별 현재·예정 계정을 확인합니다.
4. **모든 장치에 적용**을 실행하면 사전 확인 → 적용 → 검증 순서로 진행합니다. 일부 장치가 실패하면 변경한 대상을 복구하고 결과를 표시합니다. 장치 관리에서는 명시적으로 선택한 장치만 적용할 수도 있습니다.
5. **Codex CLI 관리**, **토큰 집계**, **설정**에서 CLI 업데이트, 집계와 표시 설정, 주간 기준 관련 동작을 사용할 수 있습니다. 업데이트 완료와 재연결 대기는 별도 결과로 표시됩니다.

계정을 적용하면 같은 인증 홈으로 확인한 실행 중인 Codex 데스크톱을 종료한 뒤 다시 시작합니다. 새 번들 app-server의 시작을 확인하며, 재시작 실패도 복구 대상으로 처리합니다. 같은 계정 재적용에도 이 동작을 수행합니다. 일반 CLI 작업, 다른 인증 홈의 프로세스, 무관한 TCP 서버는 종료 대상에서 제외합니다. 인증 홈을 확인할 수 없는 프로세스를 무조건 종료하지 않으며 재연결 안내 또는 작업 오류를 표시합니다.

## 서비스·로그인 세션 수명

화면과 백엔드는 별도 프로세스입니다. 창을 닫아도 백엔드는 유지되며, 앱 메뉴의 **완전히 종료** 또는 `Ctrl+Q`로 종료합니다. 작업 중에도 종료 요청을 접수하고 새로운 작업을 차단합니다. 백엔드는 진행 중인 작업의 완료 또는 취소에 따른 복구를 기다린 뒤 종료합니다. `Ctrl+R`은 상태 새로고침입니다.

화면 없이 백엔드를 명시적으로 시작하려면 다음 명령을 사용합니다.

```bash
~/.local/bin/codex-syncbar --background
```

실행기는 다음 값 중 필요한 것만 `$XDG_RUNTIME_DIR/codex-syncbar/session.env`에 기록합니다: `DISPLAY`, `WAYLAND_DISPLAY`, `DBUS_SESSION_BUS_ADDRESS`, `XAUTHORITY`, `XDG_SESSION_TYPE`, `XDG_CURRENT_DESKTOP`, `XDG_DATA_DIRS`, `CODEX_HOME`. 디렉터리는 0700, 파일은 0600이며 전체 환경이나 비밀 값을 출력하지 않습니다. 사용자 서비스의 `EnvironmentFile`로 읽고, 다른 앱에 영향을 주는 전역 `systemctl set-environment`는 사용하지 않습니다.

기본 인증 홈은 `~/.codex`입니다. 사용자 지정 홈은 첫 실행 전에 절대 경로로 전달합니다.

```bash
CODEX_HOME=/absolute/path/to/codex-home ~/.local/bin/codex-syncbar
```

이미 실행 중인 백엔드의 인증 홈이 다르면 실행기가 거부합니다. 기존 작업을 마친 뒤 백엔드를 종료하고 원하는 환경으로 다시 실행해야 합니다. 실행 중인 서비스의 세션 환경을 조용히 바꾸거나 재시작하지 않습니다. 처음 시작할 때는 실행기를 사용하세요. `systemctl --user start`만 직접 호출하면 현재 창의 세션 환경 수집을 건너뜁니다.

로그인 자동 시작은 기본적으로 꺼져 있습니다. **설정**에서 사용자가 켜면 XDG autostart 항목이 `codex-syncbar --background`를 실행합니다. 단순히 설치하거나 창을 한 번 열었다고 다음 로그인 자동 시작이 켜지지는 않습니다.

## 구조와 저장 위치

| 구성 | 책임 |
|---|---|
| `Ubuntu/ui/codex_syncbar` | GTK/libadwaita 화면, 비동기 IPC, 계정 선택·적용 미리보기 |
| `Ubuntu/CodexSyncBar.Ubuntu.Backend` | 사용자별 Unix socket, 작업 상태, 변경 직렬화, 관리 명령 |
| `Shared/CodexSyncBar.Core` | 기존 계정·사용량·SSH 코어 재사용, Linux 빌드 구성 |
| `Ubuntu/CodexSyncBar.Ubuntu.Platform` | XDG 경로, 파일 권한, Secret Service, 프로세스·브라우저·CLI 연동 |
| `Support/gpt-switch` | 원격 SSH 설치·인증 적용·검증·복구 도우미 |

기본 socket은 `$XDG_RUNTIME_DIR/codex-syncbar/control.sock`입니다. 상위 디렉터리 0700, socket 0600과 동일 사용자 peer 검사를 사용합니다. UI는 backend snapshot과 미리보기 DTO를 받으며 토큰·보관용 인증을 직접 읽지 않습니다. 미리보기 이후 설정이 달라졌으면 적용을 거부하고, 같은 요청 ID의 중복 적용은 같은 작업으로 처리합니다.

| 데이터 | 기본 위치 |
|---|---|
| 설정·암호화된 보관 프로필·복구 기록 | `${XDG_DATA_HOME:-~/.local/share}/codex-syncbar` |
| Codex가 읽는 활성 인증 | `${CODEX_HOME:-~/.codex}/auth.json` |
| 계정별 브라우저 프로필 | 상태 디렉터리 아래 `runtime/ChromeProfiles` |
| 로그인 임시 홈·관리 CLI·사용량 캐시 | 상태 디렉터리 아래 `runtime` |
| 로그인 자동 시작 항목 | `${XDG_CONFIG_HOME:-~/.config}/autostart/codex-syncbar.desktop` |

이 Ubuntu PC에 보관하는 인증·SSH 비밀·비밀 백업은 AES-GCM으로 암호화하고 마스터 키는 로그인 사용자의 Secret Service에 둡니다. `/usr/bin/secret-tool`이 없으면 패키지의 Python GI Secret 도우미를 사용합니다. 키링 오류에서는 평문 저장으로 대체하거나 기존 암호화 자료를 여는 키를 새로 만들지 않습니다. 키링 잠금 해제 후 재시도해야 합니다. Windows DPAPI로 암호화된 보관 파일을 그대로 옮겨 읽는 기능은 없습니다.

Codex 호환을 위해 **활성 인증과 CLI 실행 중인 로그인·갱신 임시 인증은 0600 JSON**입니다. CLI 종료 뒤 복구를 위해 남겨야 하는 임시 인증은 암호화된 envelope로 바꾸고, 다음 시작 때 발견한 중단된 세션의 임시 인증도 보호합니다. 로컬에 보관하는 원격 bootstrap 복구 archive는 작업별로 결합해 암호화하며, 기존의 유효한 사용자 전용 tar는 읽을 때 암호화 형식으로 이관합니다. SSH 대상에는 refresh token을 제거한 인증만 전달합니다. 원격 headless 장치에는 Secret Service를 설치하지 않으므로, 원격의 보관 프로필과 복구 checkpoint도 refresh token을 제외한 평문 JSON을 0600 권한으로 보관합니다. 원격 저장 파일까지 암호화하는 기능은 포함하지 않습니다. 계정별 브라우저 프로필도 인증 정보가 있는 개인 데이터이므로 일반 설정 파일처럼 공유하면 안 됩니다. 앱 제거만으로 계정·브라우저·복구 데이터를 삭제하지 않습니다.

## 복구와 문제 확인

계정 변경은 공통 잠금과 복구 기록으로 보호합니다. 실패한 적용은 이전 인증을 되돌린 뒤 검증하며, 복구가 끝나지 않으면 `복구 필요`를 표시하고 추가 변경을 막습니다. 시작할 때 남아 있는 복구 상태도 먼저 확인합니다. UI의 복구 재시도로 해결하고, 실패한 장치의 연결·키링·인증 상태를 확인하세요. 복구 기록이나 인증 파일을 수동으로 덮어써서 표시만 없애지 마세요.

서비스의 정상 종료는 진행 중 작업 취소와 복구를 기다립니다. 사용자 unit은 `TimeoutStopSec=5min`을 사용합니다. 강제 종료나 시스템 종료로 중단되어도 다음 실행에서 확인할 기록을 보존합니다. 실제 전원 차단을 포함한 실계정 복구 E2E는 아래 남은 검증 항목에 포함됩니다.

```bash
systemctl --user status codex-syncbar.service
journalctl --user -u codex-syncbar.service -n 80 --no-pager
```

연결할 수 없으면 실행기를 다시 열어 서비스 상태를 확인합니다. 사용량 조회 실패·오프라인에서는 마지막 정상 값과 조회 시각을 유지하므로 오래된 표시인지 확인하세요. SSH helper 설치 전 필수 도구를 확인하며, 현재 상태 조회는 번들 도우미를 stdin으로 실행해 원격 파일 설치·인증 변경 없이 수행합니다. 원격 계정 슬롯과 실제 인증 지문이 맞지 않으면 적용을 차단합니다.

## 실행한 QA

결과는 [자동 통합 검증 기록](qa-evidence/automated-integration.json), [실제 OpenSSH 루프백 기록](qa-evidence/ssh-loopback-smoke.json), [600×480 GTK 통합 기록](qa-evidence/gtk-integration-600x480.json)에 있습니다. 다음은 해당 기록 시점에 실제 실행한 검사입니다.

| 검사 | 결과·범위 |
|---|---|
| 기존 Windows 공용 코어를 Linux에서 실행 | 245 통과, Windows 전용 12 건너뜀 |
| Ubuntu 코어·플랫폼·운영 서비스 | 최종 재시작·종료 복구 수정 후 98 통과, Windows 전용 1 건너뜀 |
| 최종 변경 후 인증·전환·운영 서비스·복구 암호화 집중 회귀 | 51 통과, Windows 전용 1 건너뜀; 위 전체 실행과 중복되는 검사 포함 |
| 최종 공유 코드 변경 후 기존 Windows 공용 코어 집중 회귀 | Linux에서 30 통과, Windows 전용 1 건너뜀 |
| Python 클라이언트·실행기 | 8 통과 |
| 사용자 설치기 | 5 통과 |
| 실계정 QA supervisor·해시 내보내기·GTK driver 경계의 격리 fixture | 최종 21 통과 |
| GTK 캡처 준비·실제 PNG·사용자 전용 권한 | 소유한 격리 창에서 1 통과 |
| Secret GI 도우미 fixture | 4 통과 |
| 전용 세션 환경 전달 | 6 통과 |
| 배포 빌드 및 설치된 바이너리의 데모 IPC | 각각 11 통과 |
| usage-summary·셸 helper·동시 수집·원격 CLI 업데이트 | 기존 회귀 검증 통과 |
| 실제 OpenSSH 루프백 전송 | 17 통과, 임시 홈·생성한 키·가상 인증 사용 |
| GTK 자체 위젯 신호 + 실제 데모 IPC | 600×480에서 9 통과 |
| 실제 마우스·Tab 기본 조작 | 사용자가 계정 선택기·메뉴 열기와 Tab focus 이동 확인 |
| 패키지 | `.deb` 추출·의존성·.NET 런타임·기본 자동 시작 꺼짐 검사 통과 |

운영 서비스 검증은 격리된 실제 인증 파일과 실제 `gpt-switch`를 사용했습니다. 로컬 A→B→A, 로컬/SSH 단일 대상, 미리보기 무효화, 중복 요청, 원격 적용 후 검증 실패와 양쪽 복구를 확인했습니다. SSH 프로세스 경계는 소유한 fixture로 대체했고, 실제 암호화 SSH 전송은 별도의 루프백 검증으로 확인했습니다. 실행 중 프로세스 종료 검사는 명시적으로 만든 임시 프로세스만 대상으로 했습니다.

최종 변경 회귀에서는 전체 이메일 표시, 예약한 로그인 profile ID 유지, 기준 신원 해시의 정확성·토큰 미노출·인증 파일 불변성을 확인했습니다. 원격 복구 archive의 암호화·작업별 결합·변조 거부·기존 tar 이관, 남은 로그인/갱신 인증의 암호화 및 복구 가능 여부도 임시 데이터로 검증했습니다. supervisor fixture는 실제 재시작 증거를 위해 같은 인증 홈의 프로세스 PID와 시작 시각이 바뀌었는지 확인하는 계약을 포함합니다. fixture 통과만으로 실제 Codex 재시작이 검증된 것은 아닙니다. 최종 회귀는 Electron이 루트 프로세스 환경을 지운 경우의 동일 사용자·번들 app-server 자식 기반 인증 홈 확인, 다른 인증 홈·일반 CLI·재사용 PID 제외, 재시작용 GUI 환경 전달, supervisor의 빈 데스크톱 기준 거부 및 작업 잠금 해제 대기를 추가로 확인했습니다.

GTK 검증은 운영 관리 화면 다섯 개 및 SSH 양식을 생성하고, 빠른 계정 선택, 선택만으로 인증이 바뀌지 않는 동작, 중복 적용 차단, IPC를 통한 A→B→A, 계정 선택기·마지막 장치 행의 focus 가능 여부와 스크롤 끝 도달을 확인했습니다. 브라우저 로그인을 기다릴 때 적용 중 spinner 대신 로그인 창 다시 열기·취소를 표시하는 회귀 검사도 포함합니다. 이는 앱 내부 GTK 신호·위젯 검사입니다. 실제 마우스·키보드 입력 검증으로 간주하지 않습니다.

이와 별도로 사용자가 실제 마우스로 계정 선택기와 메뉴를 열고 Tab으로 보이는 focus가 이동하는 것을 확인했습니다. [사용자 직접 조작 기록](qa-evidence/native-manual.json)은 이 두 항목을 기록하며, 모든 관리 양식의 키보드 순회나 150% 배율·다중 모니터까지 검증했다는 뜻은 아닙니다.

화면 기록은 앱의 `Gtk.WidgetPaintable`을 사용했습니다. [기본 화면](qa-evidence/native-default.png), [밝은 테마](qa-evidence/native-light.png), [어두운 테마](qa-evidence/native-dark.png), [600×480](qa-evidence/native-small.png), [작은 창의 하단](qa-evidence/native-small-bottom.png), [2배 배율](qa-evidence/native-hidpi.png), [오프라인](qa-evidence/native-offline.png)을 기록했습니다. 가상 계정 화면이며 실제 로그인 완료의 증거는 아닙니다.

[Ubuntu CI](../../.github/workflows/ubuntu.yml)는 격리 테스트, OpenSSH 루프백, 패키지 추출 검사를 실행하도록 추가했습니다. 이 작업 중 GitHub Actions 원격 실행 결과는 확인하지 않았습니다. CI의 Ubuntu 24.04 runner는 통합 테스트와 빌드·패키지 추출용이며, Ubuntu 26.04 `.deb`를 설치해서 실행한 증거가 아닙니다.

### 데모와 GTK 검증 재현

빌드 후 데모 화면을 여는 가장 간단한 방법입니다.

```bash
bash Scripts/Ubuntu/run.sh --demo
```

소스 실행기에서 `--demo`를 생략하면 운영 백엔드가 실제 사용자 경로를 사용합니다. 화면 확인에는 `--demo`를 사용하세요. 데모 백엔드는 명시적 별도 socket을 요구하며 메모리의 가상 계정만 사용합니다.

로그인된 GUI 세션에서 실제 데모 backend와 GTK harness를 함께 실행하려면 다음과 같이 합니다. 종료 시 여기서 시작한 데모 프로세스만 정리합니다.

```bash
demo_root=$(mktemp -d)
chmod 700 "$demo_root"
dist/ubuntu/backend/CodexSyncBar.Backend --demo \
  --socket "$demo_root/control.sock" >"$demo_root/backend.log" 2>&1 &
demo_pid=$!
trap 'kill "$demo_pid" 2>/dev/null || true; wait "$demo_pid" 2>/dev/null || true; rm -rf "$demo_root"' EXIT
for attempt in {1..100}; do
  test -S "$demo_root/control.sock" && break
  sleep 0.05
done

PYTHONPATH=Ubuntu/ui /usr/bin/python3 -m codex_syncbar.qa \
  --socket "$demo_root/control.sock" --width 600 --height 480 \
  --output dist/ubuntu/tests/gtk

PYTHONPATH=Ubuntu/ui /usr/bin/python3 -m codex_syncbar \
  --demo --socket "$demo_root/control.sock" --profile-id 2 \
  --capture dist/ubuntu/tests/demo.png
```

실제 OpenSSH 전송만 재현하려면 일반 사용자로 `bash Tests/ssh-loopback-smoke.sh /path/to/sshd`를 실행합니다. 생성한 키, loopback listener, 강제된 임시 HOME/CODEX_HOME을 사용합니다. 시스템 sshd를 설정하거나 실제 사용자 SSH 인증을 바꾸는 절차가 아닙니다.

## 실계정·데스크톱 E2E 상태

실계정 검증 기록은 원본 작업 디렉터리에 별도 보관합니다. 아래 도구는 명시적으로 요청한 실환경 검증에만 사용하며 CI의 격리 테스트와 구분합니다.

[`Ubuntu/tests/live_qa.py`](../../Ubuntu/tests/live_qa.py)는 이를 위한 명시적 선택형 서비스 통합 supervisor입니다. 기본 모드는 사전 확인이며 적용을 실행하지 않습니다. 실행 전 등록 profile ID에 대응하는 독립적으로 확인한 `accountIdSha256`·`emailSha256` JSON, 정확한 SSH 장치 ID, 복구 가능한 원래 상태가 필요합니다. 상세 인자는 다음 명령으로 확인합니다.

```bash
python3 Ubuntu/tests/live_qa.py --help
```

`--execute`는 실제 적용을 허용하는 옵션이며, 데스크톱과 독립된 `codex-syncbar-live-qa-*.service` systemd 사용자 unit에서 실행되는지 검사합니다. dry-run의 활성 대상 집합이 요청한 장치와 정확히 일치하고 각 장치의 개별 복구가 가능할 때만 후속 실행을 진행합니다. supervisor는 원본 토큰을 증거에 저장하지 않고 해시와 작업 결과를 남기며, 실패 시에도 서비스의 개별 적용 경로로 원래 계정과 로컬 데스크톱 실행 상태 복구를 시도합니다. 복구가 막히면 인증 파일을 직접 덮어쓰지 않고 주의가 필요한 상태로 종료합니다. 이 스크립트의 fixture 통과는 실제 호스트 전환 통과와 다릅니다.

준비한 계정의 기준 해시는 [`live_qa_hashes.py`](../../Ubuntu/tests/live_qa_hashes.py)로 운영 서비스의 읽기 전용 `account.identityHashes` 응답에서 내보낼 수 있습니다. 서비스가 암호화 보관 프로필을 읽어 계산하며 스크립트에는 원본 토큰·account ID·이메일이 전달되지 않습니다. 내보낸 파일은 0600이고 두 개 이상의 서로 다른 등록 계정을 요구합니다. 이 기준과 각 장치의 실제 CLI·활성 인증 관측을 비교합니다. supervisor의 복구 범위는 **SSH 준비가 끝난 뒤 기록한 계정 신원과 데스크톱 실행 상태**이며, 그 전에 설치한 원격 helper나 토큰의 원래 바이트를 되돌리는 기능은 아닙니다.

선택적으로 [`live_gtk_apply.py`](../../Ubuntu/tests/live_gtk_apply.py)를 지정하면 진행 방향 A→B→A를 운영 GTK 계정 선택기와 적용 버튼의 자체 신호를 통해 실행합니다. 신원 검증과 실패 시 복구는 독립 supervisor가 맡습니다. 이 경로 역시 외부 포인터·키보드 입력 자동화가 아니며, 실제 실행 결과가 있어야 실계정 GTK 통합 검증으로 기록할 수 있습니다.

현재 확인한 CLI `account/read` 응답에는 workspace account ID가 없으므로 SDK 결과만으로 해당 ID를 검증했다고 주장하지 않습니다. 로컬/원격 활성 인증의 account ID 해시와 CLI 응답의 계정 종류·이메일 해시를 함께 확인합니다. 계정 전환 E2E 통과 범위와 위에서 남긴 수동 사용성·실환경 오류 검증 범위를 구분합니다.
