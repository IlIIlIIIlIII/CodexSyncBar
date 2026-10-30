# Claude SyncBar for Ubuntu

**여러 Claude 계정의 5시간·주간 사용량을 확인하고, 이 Ubuntu PC의 Claude 데스크톱 앱과 Claude Code, SSH 장치의 Claude Code 로그인을 한 번에 전환하는 네이티브 데스크톱 앱입니다.**

`claude` 브랜치는 `main`의 Codex SyncBar를 Claude Code용으로 옮긴 것입니다. 앱 ID·실행 파일·서비스·데이터 디렉터리가 모두 `claude-syncbar`로 달라 두 앱을 함께 설치할 수 있습니다.

[Ubuntu 설치·빌드 안내](docs/ubuntu/README.md) · [플랫폼 지원 정책](docs/platform-support.md)

![계정별 5시간·주간 잔여량 비교 — 가상 계정을 쓰는 데모 화면](docs/ubuntu/claude-demo.png)

## 주요 기능

- 첫 화면에서 모든 계정의 **5시간·주간 잔여율과 초기화 시각** 비교, 조회 실패·이전 수치 구분
- 계정 행을 눌러 전환 화면으로 이동, 계정 추가·재로그인·별칭 관리
- 현재 계정과 전환할 계정, 로컬·SSH 장치별 적용 예정 상태 확인
- 여러 장치에 계정 적용, 실패 시 복구, Claude Code CLI 관리
- **스마트 전환** 버튼: 계정 목록에서 체크한 후보(처음에는 Max 20x 계정) 중 주간 한도가 남고 5시간 한도를 75% 미만 쓴 계정에서 주간 초기화가 가장 가까운 계정으로 한 번에 전환
- **자동 전환**(선택): 지금 계정의 5시간이나 주간 한도가 0%가 되면 우선순위에서 쓸 수 있는 다음 계정으로 모든 활성 장치를 전환
- **멈춘 세션 자동 재개**(선택): 사용량 한도로 멈춘 Claude Code 세션을, 사용량이 다시 생기면 백그라운드에서 이어서 실행
- 이 PC의 **Claude 데스크톱 앱**도 함께 전환. 세션 목록과 설정은 그대로 두고 로그인만 바꿉니다 (계정마다 처음 한 번만 데스크톱에서 로그인)
- Claude Code 세션 기록 기반 토큰 집계와 API 요금 환산 추정
- GTK 4·libadwaita 기반 Yaru 화면, 상단 바에 `5시간 72% · 주간 68%`를 표시하고 계정별 잔여량과 전환을 보여 주는 GNOME 팝업
- 창을 닫아도 유지되는 사용자 서비스. 상단 바 확장을 켜 두면 로그인하자마자 확장이 서비스를 시작하고, 확장 없이 쓸 때는 선택적인 로그인 자동 시작

## 시작하기

배포 대상은 **Ubuntu 26.04 amd64**입니다. 빌드에는 .NET 10 SDK와 Node.js가 필요하며, 실행에는 GTK 4·libadwaita·PyGObject·GNOME Secret Service와 systemd 사용자 세션, 그리고 Claude Code CLI가 필요합니다.

```bash
bash Scripts/Ubuntu/build.sh
bash Scripts/Ubuntu/package.sh --no-build
```

관리자 권한 없는 사용자 설치:

```bash
bash Scripts/Ubuntu/install-user.sh
~/.local/bin/claude-syncbar
```

계정 선택만으로 실제 로그인이 바뀌지는 않으며 적용 동작을 실행해야 합니다.

## Codex 버전과 다른 점

| 항목 | 동작 |
|---|---|
| 전환 대상 | Claude Code CLI의 로그인(`~/.claude/.credentials.json`의 `claudeAiOauth`, `~/.claude.json`의 `oauthAccount`). 두 파일의 다른 내용은 그대로 둡니다. |
| 사용량 | 5시간(`five_hour`)과 주간(`seven_day`) 한도를 함께 표시합니다. 설정에서 둘 중 하나를 숨길 수 있습니다. 모델별 주간 한도는 읽지 않습니다. |
| 초기화권 | Claude에 해당 개념이 없어 제거했습니다. |
| 실행 중인 세션 | 종료하지 않습니다. 전환 시 실행 중이던 Claude Code 세션은 다시 시작하라고 안내합니다. |
| Claude 데스크톱 앱 | CLI 로그인을 공유하지 않습니다. 프로필은 하나만 두고, 전환할 때 앱을 종료한 뒤 로그인 쿠키 파일과 앱이 그 로그인으로 받은 토큰만 그 계정의 것으로 바꿔 다시 시작합니다. 토큰까지 보관된 계정은 전환 후 다시 로그인하라는 요청을 받지 않습니다. 세션 기록은 활성 계정 이름의 폴더로 옮겨 어느 계정에서나 같은 목록이 보이게 하고, 계정별로 저장되는 권한 설정도 넘겨줍니다. 커넥터와 계정 설정은 Anthropic 서버에 계정별로 있어 옮기지 못합니다. 자세한 동작은 [Ubuntu 안내](docs/ubuntu/README.md#claude-데스크톱-앱-계정-전환)에 있습니다. |

## 검증 상태

격리 테스트와 데모 화면은 이 브랜치에서 통과했습니다. 실제 계정으로는 계정 4개의 로그인, 주간 사용량 조회(5시간 표시는 실제 계정으로 아직 확인 전), 주간 시작 메시지, 그리고 이전 방식(계정별 프로필 폴더)의 데스크톱 전환까지 확인했습니다. **지금 방식(프로필 하나, 로그인만 교체)의 데스크톱 전환은 임시 인스턴스로 핵심 동작만 확인했고, 실제 전환은 아직 실행해 보지 않았습니다.** 범위와 남은 항목은 [Ubuntu 안내의 검증 상태](docs/ubuntu/README.md#검증-상태)에 있습니다.

## 이전 플랫폼

macOS·Windows 소스는 Codex 버전의 보관본이며 이 브랜치에서 포팅하지 않았습니다. `docs/`의 `qa-evidence`, `design-qa` 문서와 `docs/windows`, `docs/macos`도 Codex 버전의 기록입니다.

> Anthropic의 공식 제품이 아닌 개인용 유틸리티입니다. 공개 문서가 없는 사용량 응답 형식에 의존하므로 형식이 바뀌면 수정이 필요합니다. 여러 계정을 사용하는 방식이 Anthropic 이용약관에 맞는지는 사용자가 확인해야 합니다.
