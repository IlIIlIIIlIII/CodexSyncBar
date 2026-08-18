# Codex SyncBar

<p align="center">
  <img src="Resources/AppIcon.png" width="128" alt="Codex SyncBar 아이콘">
</p>

여러 ChatGPT/Codex 계정을 이 Mac/Windows PC와 SSH 장치에서 한 번에 전환하는 계정 관리 앱입니다. macOS에서는 SwiftUI 메뉴 막대 앱으로, Windows에서는 .NET 10 + WinUI 3 데스크톱 앱으로 동작합니다. macOS는 계정별 Chrome 로그인 세션을 사용하고, Windows는 사용자가 지정한 기본 브라우저로 로그인합니다.

> 이 프로젝트는 개인용 유틸리티이며 OpenAI의 공식 제품이 아닙니다. ChatGPT 사용량 응답은 공개된 안정 API가 아니므로 OpenAI의 응답 형식이 바뀌면 일부 표시가 일시적으로 동작하지 않을 수 있습니다.

## 사용자 UI

아래 화면의 계정, 장치와 사용량은 README를 위해 만든 가상 예시이며 실제 사용자 정보가 아닙니다.

### 메뉴 막대 화면

계정을 선택하면 사용량, 초기화권과 모든 장치의 적용 상태를 한눈에 확인할 수 있습니다.

<p align="center">
  <img src="docs/images/readme-popover.png" width="420" alt="가상 데이터로 구성한 Codex SyncBar 메뉴 막대 화면">
</p>

### 설정 화면

계정의 별칭과 표시 순서를 관리하고, 필요한 계정만 다시 로그인하거나 로그아웃할 수 있습니다.

<p align="center">
  <img src="docs/images/readme-settings.png" width="760" alt="가상 데이터로 구성한 Codex SyncBar 계정 설정 화면">
</p>

## 주요 기능

- 계정을 두 개 이상 등록하고 별칭과 표시 순서를 관리합니다.
- 5시간, 주간, Spark 5시간, Spark 주간 한도를 막대 형태로 표시합니다.
- 메뉴 막대에는 원하는 사용량 항목을 0~2개까지 선택해 표시합니다.
- 초기화권 수량과 다음 만료 시각을 보기 쉽게 표시합니다.
- 선택한 계정을 이 Mac과 등록된 SSH 장치에 한 번에 적용합니다.
- 장치별 연결 상태, 적용 계정, 최근 30일 토큰 사용량과 API 가격 기준 추정 비용을 보여 줍니다.
- macOS는 계정마다 별도의 영구 Chrome 프로필을 사용하고, Windows는 기본 브라우저의 Google 로그인·패스키 흐름을 사용합니다.
- 로그인, 로그아웃, 인증 새로고침, 시작 프로그램 및 SSH 장치 관리는 설정 창에 모아 두었습니다.
- 실험 기능으로 Cursor SDK 구독 모델을 로컬 Responses 브리지로 Codex의 기존 모델 선택기에 추가합니다. macOS에서는 SyncBar의 브라우저 로그인으로 구독을 연결하고, Codex 앱의 추론 강도·Fast 선택을 실제 Cursor SDK variant로 변환합니다. 원래 설정으로 복구할 수도 있습니다.

## 플랫폼 지원

현재 저장소는 macOS SwiftUI 메뉴 막대 앱과 Windows .NET 10 + WinUI 3 데스크톱 앱을 함께 포함합니다.

Windows 앱은 다음 핵심 흐름을 지원합니다.

- Codex `app-server` 로그인 프로토콜과 Windows 기본 브라우저 로그인
- 계정별 `auth.json` 보관, 계정 전환, 별칭 편집, 로그아웃·재로그인과 계정 항목 삭제
- ChatGPT/Codex 5시간·주간·Spark 사용량과 초기화권 표시
- Windows 로컬 Codex 인증 적용 및 OpenSSH/scp 기반 SSH 장치 동기화
- 모든 장치의 사전 검증·전환·사후 검증과 실패 시 로컬·원격 rollback
- 최근 30일 Codex 세션 토큰 집계와 모델별 공개 가격 기준 추정 비용 표시
- `cursor-agent --list-models`를 이용한 Windows 로컬 Cursor Responses 브리지와 Codex provider `config.toml` 활성화·원복
- Cursor 모델 exact slug, Thinking 변형, CLI 경로, 설치 안내·로그인 명령 복사와 SSH 원격 provision/deprovision
- Windows 10/11의 WinUI 3 설정 창과 장치 연결 상태 확인
- Windows 알림 영역 트레이와 macOS 메뉴 막대식 빠른 보기(좌클릭으로 열기/닫기, 포커스를 잃으면 자동 닫기), 로그인 시 자동 시작, 5분 사용량·30분 장치 상태·1시간 인증 유지·6시간 전체 동기화
- 계정별 주간 anchor, 선택 계정·전체 새로고침, Codex 실행 중 인증 갱신 지연 및 다음 점검 재시도
- 중단된 계정 로그아웃·장치 활성화·비밀 삭제·이전 브라우저 세션 정리를 다음 실행에서 복구하는 durable journal
- Windows DPAPI 기반 SSH 비밀번호·키 암호·Cursor User API Key 저장과 원격 Cursor provision/deprovision

Windows의 Cursor 브리지는 로컬 Cursor CLI(`cursor-agent status`)를 사용하며, Cursor CLI가 설치·로그인되어 있어야 합니다. Windows 토큰 집계는 앱에 포함된 `usage-summary.mjs`를 실행하고, 등록된 SSH 장치는 원격 helper가 없거나 연결할 수 없으면 해당 장치만 실패로 표시합니다. SSH Cursor 원격 provisioning/deprovisioning도 지원하며, Cursor User API Key와 SSH 비밀번호·키 암호는 Windows DPAPI로 사용자 계정에 묶어 저장합니다. SSH 비밀은 OpenSSH 자식 프로세스의 askpass 환경으로만 전달되고 명령 인자나 로그에는 넣지 않습니다. 두 플랫폼은 동일한 계정/장치 개념과 원격 helper 계약을 사용하지만 실제 저장 위치와 인증 세션은 플랫폼별로 분리됩니다.

### Windows에서 빌드하기

필요한 환경은 Windows 10 1809 이상, .NET 10 SDK, Windows App SDK 2.4를 사용하는 WinUI 3 빌드 환경, Node.js, Windows 기본 브라우저, 공식 Codex CLI, Windows OpenSSH 클라이언트입니다. 로그인 URL은 기본 브라우저로 열립니다. Cursor 브리지를 사용할 때는 공식 Cursor CLI와 완료된 `cursor-agent login`도 필요합니다. .NET SDK가 NuGet에서 Windows App SDK 패키지를 복원하므로 별도의 Swift/Xcode 환경은 필요하지 않습니다.

```powershell
dotnet restore Windows\CodexSyncBar.Windows.sln --runtime win-x64
dotnet test Windows\CodexSyncBar.Windows.Core.Tests\CodexSyncBar.Windows.Core.Tests.csproj
dotnet build Windows\CodexSyncBar.Windows\CodexSyncBar.Windows.csproj --configuration Release --property:Platform=x64 --runtime win-x64
dotnet publish Windows\CodexSyncBar.Windows\CodexSyncBar.Windows.csproj --configuration Release --property:Platform=x64 --runtime win-x64 --self-contained false
dotnet run --project Windows\CodexSyncBar.Windows\CodexSyncBar.Windows.csproj --configuration Release --property:Platform=x64 --runtime win-x64 --no-restore
```

Visual Studio에서 열 때는 `Windows\CodexSyncBar.Windows.sln`을 사용하고 `x64`, `x86`, `ARM64` 중 대상 장치에 맞는 구성을 선택합니다. RID별 빌드는 해당 런타임으로 복원한 뒤 실행합니다.
기본 `dotnet run`은 MSIX 프로필을 사용하므로 개발 PC에서는 Windows Developer Mode가 필요합니다. Visual Studio의 `CodexSyncBar.Windows (Unpackaged)` 프로필은 패키지 identity 없이 실행할 때 사용하고, 배포 시에는 Visual Studio의 MSIX 패키징/설치 흐름을 사용합니다.
이미 실행 중인 이전 Debug/설치본이 있으면 먼저 완전히 종료한 뒤 Release 명령으로 실행해야 최신 로그인 수정이 반영됩니다.

일반 실행 시 큰 관리 창 대신 작업 표시줄 알림 영역 가까이에 빠른 보기가 열립니다. 바깥을 클릭하면 닫히며, 이후에는 SyncBar 트레이 아이콘을 좌클릭해 다시 열거나 닫을 수 있습니다. 빠른 보기에서 계정 선택, 사용량·장치 확인, 새로고침, 전체 장치 계정 전환을 처리하고, 로그인·계정 추가·SSH 설정은 하단의 **전체 관리** 또는 트레이 우클릭 메뉴의 **전체 관리 창 열기**를 사용합니다.

```powershell
dotnet restore Windows\CodexSyncBar.Windows.sln --runtime win-x86
dotnet build Windows\CodexSyncBar.Windows.sln --configuration Release --property:Platform=x86 --runtime win-x86 --no-restore
dotnet restore Windows\CodexSyncBar.Windows.sln --runtime win-arm64
dotnet build Windows\CodexSyncBar.Windows.sln --configuration Release --property:Platform=ARM64 --runtime win-arm64 --no-restore
```

개발·문서 QA 실행도 macOS 앱과 같은 인자 계약을 제공합니다. `--preview-window`는 QA 화면을 열고, `--login-profile=<양의 정수>`는 해당 계정의 기본 브라우저 로그인 흐름을 바로 시작합니다. README 캡처는 기존 디렉터리에 대한 절대 PNG 경로만 허용합니다.

```powershell
& .\Windows\CodexSyncBar.Windows\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\CodexSyncBar.Windows.exe --preview-window
& .\Windows\CodexSyncBar.Windows\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\CodexSyncBar.Windows.exe --login-profile=2
& .\Windows\CodexSyncBar.Windows\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\CodexSyncBar.Windows.exe --readme-demo=popover --readme-output=C:\Users\Public\codex-syncbar-popover.png
& .\Windows\CodexSyncBar.Windows\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\CodexSyncBar.Windows.exe --readme-demo=settings --readme-output=C:\Users\Public\codex-syncbar-settings.png
```

Windows 앱은 다음 위치를 기본으로 사용합니다.

- 설정과 프로필: `%LOCALAPPDATA%\CodexSyncBar`
- Codex 활성 인증: `%USERPROFILE%\.codex\auth.json`
- 로그인·인증 갱신 임시 홈: `%USERPROFILE%\.codex-syncbar\LoginSessions` (WinUI 패키지 파일 가상화 밖에서 Codex CLI와 공유)
- 로그인 브라우저: Windows 기본 브라우저가 쿠키와 세션을 관리합니다.
- 이전 버전 격리 Chrome 세션(호환 정리용): `%LOCALAPPDATA%\CodexSyncBar\ChromeProfiles`
- DPAPI 비밀과 복구 journal: `%LOCALAPPDATA%\CodexSyncBar\secrets`, `%LOCALAPPDATA%\CodexSyncBar\login-transactions`, `%LOCALAPPDATA%\CodexSyncBar\logout-transactions`, `%LOCALAPPDATA%\CodexSyncBar\device-activation-transactions`, `%LOCALAPPDATA%\CodexSyncBar\secret-cleanup-transactions`, `%LOCALAPPDATA%\CodexSyncBar\remote-bootstrap-transactions`

기존에 `%USERPROFILE%\.local\share\gpt-switch\config.json`이 있으면 계정/장치 목록을 먼저 호환 경로로 읽습니다. 상태 루트를 명시하려면 `CODEX_SYNCBAR_STATE_ROOT`를, Codex 홈을 명시하려면 `CODEX_HOME`을 설정하세요.

## 설치 전 확인

- macOS 13 Ventura 이상
- `/Applications/Google Chrome.app`에 설치된 Google Chrome
- 공식 Codex CLI (`/opt/homebrew/bin/codex`, `/usr/local/bin/codex` 또는 `~/.local/bin/codex`)
- 로컬 및 SSH 장치의 `bash`, `jq`, `node`, `tar`
- Cursor 구독 모델 브리지를 이 Mac에서 사용할 경우 Cursor 앱에 포함된 Node.js 22.13 이상 또는 같은 버전 조건을 만족하는 별도 Node.js
- SSH 장치를 사용할 경우, 해당 호스트의 키를 미리 `known_hosts`에 등록해야 합니다.

## 릴리즈로 설치하기

1. [Releases](https://github.com/IlIIlIIIlIII/CodexSyncBar/releases)에서 최신 `Codex-SyncBar-*-macOS-universal.zip`을 받습니다.
2. ZIP을 풀고 `Codex SyncBar.app`을 `/Applications`로 옮깁니다.
3. Finder에서 앱을 엽니다. 1.0.4 이후 공개 릴리즈는 Apple 공증을 거치므로 별도의 Gatekeeper 우회가 필요하지 않습니다.
4. 메뉴 막대의 Codex SyncBar를 열고 **설정 → 계정 → 계정 추가**를 선택합니다.

1.0.4 이후 공개 릴리즈는 Developer ID로 서명하고 Apple 공증을 통과한 뒤 공증 티켓을 앱에 첨부합니다. 다운로드한 ZIP을 풀어 `/Applications`로 옮기면 Gatekeeper가 개발자 신원과 공증 상태를 확인할 수 있습니다. 로컬 `./build-app.sh` 실행은 Developer ID가 없으면 기존처럼 ad-hoc 서명을 사용합니다.

원한다면 릴리즈에 함께 첨부된 `SHA256SUMS`로 다운로드 파일을 확인할 수 있습니다.

```bash
shasum -a 256 -c SHA256SUMS
```

## 처음 설정하기

### 1. 계정 추가

설정의 **계정** 페이지에서 계정을 추가하면 macOS에서는 앱 전용 Chrome 창이, Windows에서는 기본 브라우저가 열립니다. 로그인 완료 전까지 기존 `auth.json`은 바뀌지 않습니다. 새 인증이 완전한 Codex 계정이고 다른 슬롯과 중복되지 않는지 확인한 뒤에만 계정 목록에 반영합니다.

각 계정은 고정된 양의 ID를 사용합니다.

- 인증 파일: `~/.local/share/gpt-switch/profiles/<계정 ID>.auth.json`
- Chrome 프로필: `~/Library/Application Support/Codex SyncBar/ChromeProfiles/profile-<계정 ID>`

별칭 변경이나 순서 이동은 표시만 바꾸며 인증 파일과 Chrome 세션의 위치는 바꾸지 않습니다.

### 2. SSH 장치 추가

설정의 **장치** 페이지에서 호스트, 포트, 사용자 이름과 인증 방법을 입력합니다. 새 장치는 비활성 상태로 저장되며 **설치 및 활성화**가 성공해야 전체 전환 대상에 포함됩니다.

지원하는 인증 방법은 다음과 같습니다.

- 기존 OpenSSH 설정
- 개인 키와 선택적 OpenSSH 인증서·키 암호
- SSH 비밀번호

활성화할 때 앱은 원격 helper를 설치하고, 등록된 계정을 전송한 뒤 [공식 standalone installer](https://learn.chatgpt.com/docs/codex/cli)로 Codex CLI를 설치하거나 업데이트합니다. 마지막으로 현재 계정과 Codex CLI 로그인 상태가 실제로 적용됐는지 검증합니다. 중간 단계가 실패하면 새 장치를 활성화하지 않고 가능한 범위에서 이전 helper와 계정 상태를 복구합니다. Cursor provider가 활성 상태라면 장치 활성화 직후 고정된 Cursor SDK runtime·bridge·manager도 설치하고 인증 상태를 확인합니다. 새로 설치한 앱에는 SSH 장치가 미리 등록되어 있지 않습니다.

### 3. 계정 전환

메뉴의 계정 버튼을 누르고 **모든 장치에 전환**을 선택하면 추가 확인 창 없이 전환을 시작합니다. 모든 장치를 먼저 점검한 뒤 변경하며, 실패하면 이미 변경된 장치를 이전 계정으로 되돌립니다.

전환 후에는 이전 인증을 캐시할 수 있는 Codex `app-server`만 선별해 다시 시작합니다. 실행 중인 일반 Codex CLI 작업과 TCP 기반의 별도 서버는 종료하지 않습니다.

### 4. Cursor 구독 모델 연결 (실험)

1. SyncBar의 **설정 → 모델**에서 **Cursor 구독으로 로그인**을 누르고 브라우저에서 현재 Cursor 구독 계정 로그인을 완료합니다. 수동 User API Key 입력은 사용하지 않습니다.
2. SyncBar는 Cursor SDK가 발급한 만료형 자격증명으로 계정과 사용 가능한 모델·variant를 확인하고, 자격증명은 이 Mac의 기기 전용 Keychain에 저장합니다. SDK의 기본 인증 파일에는 별도로 저장하지 않습니다.
3. base 모델과 필요한 경우 Thinking, localhost 포트를 선택한 뒤 **Codex 기본 모델로 사용**을 누릅니다.
4. SyncBar가 기존 Codex 모델을 보존한 병합 카탈로그를 만들고 최상위 `model_catalog_json`에 연결합니다. Cursor의 exact variant는 기본 모델과 Thinking 여부로 묶어 `Cursor · GPT-5.6 Sol`, `Cursor · Opus 4.6`처럼 표시합니다. Reasoning과 Fast는 Codex 앱의 기존 선택창에서 고르며, 목록에 실제로 존재하는 조합만 광고하고 exact Cursor SDK variant로 변환합니다.
5. SyncBar가 캐시된 로컬 Codex `app-server`만 종료해 시작 시 카탈로그와 설정을 다시 읽게 합니다. 새 Codex 작업은 `syncbar_cursor_bridge` provider를 사용하고, 실행 중인 일반 Codex CLI는 종료하지 않습니다.
   활성화 전에 만든 OpenAI 작업은 관리되는 `openai_base_url`을 통해 같은 로컬 브리지로 들어오므로, 작업 안에서 Cursor 모델로 바꿔도 ChatGPT 모델 검증 오류 없이 동작합니다. 기존 OpenAI 모델 요청은 인증 헤더를 보존한 채 공식 OpenAI/ChatGPT endpoint로만 전달합니다.
6. SSH에서도 사용할 때는 같은 SDK 발급 자격증명을 **활성 SSH 장치에 동기화**로 보냅니다. SSH 명령 인자에는 넣지 않고 stdin으로만 전달하며, 수동 User API Key는 받지 않습니다.
7. 원격 호스트에는 필요할 때 공식 Cursor 설치 스크립트의 Node 런타임을 준비하고, SyncBar가 고정한 Cursor SDK runtime·bridge·manager와 격리된 상태 저장소를 구성합니다. macOS Keychain 파일을 Linux에 복사하거나 변환하지 않습니다.
8. **이전 Codex 모델로 복구**를 누르면 SyncBar가 바꾼 로컬 및 연결 가능한 SSH 장치의 최상위 `model`·`model_provider`·`model_catalog_json`과 관리 provider 블록을 원래 값으로 되돌리고, 원격 전용 runtime·Cursor 인증 저장소도 제거합니다. 설정이 외부에서 바뀌었다면 덮어쓰지 않고 해당 장치를 정리 실패로 표시합니다.
9. 설정의 **Cursor 계정**에서 SDK 구독 로그인 이메일을 확인하고, **Cursor 사용량 로그인**으로 SyncBar 전용 웹 프로필을 인증하면 Cursor Models·Other Models의 월간 잔여량과 초기화 시각을 계정 카드에서 볼 수 있습니다. **계정 연결 삭제**는 provider 복구, 브리지 중지, 사용량 웹 세션, Keychain SDK 자격증명 및 원격 자격증명 정리를 수행하지만 Cursor.com 웹 계정과 구독 자체는 삭제하지 않습니다.
10. 설정의 Codex 계정 행에서 휴지통을 누르면, 필요 시 다른 로그인 계정으로 모든 장비를 안전하게 전환하고 로그아웃한 뒤 해당 인증·전용 Chromium 세션·SyncBar 계정 항목을 제거합니다. OpenAI·ChatGPT 웹 계정과 구독 자체는 삭제하지 않으며, 안전한 폴백을 위해 마지막 한 계정은 남겨 둡니다.

SyncBar 실행 환경에 절대 경로 `CODEX_HOME`이 있으면 해당 `config.toml`을 사용하고, 그렇지 않으면 `~/.codex/config.toml`을 사용합니다. 실제 대상 경로는 설정 화면에 표시합니다.

브리지는 로컬과 원격 모두 `127.0.0.1`에만 바인딩하며 요청마다 비공개 고엔트로피 bearer/header를 검증합니다. macOS와 SSH의 Cursor 요청은 Cursor SDK의 기본 코딩 프롬프트와 agent mode를 유지합니다. Codex의 시스템·개발자 지침은 전용 빈 작업공간의 격리된 project rule로, Codex 도구는 SDK callback tool로 매핑합니다. 실제 프로젝트나 사용자의 Cursor 전역 rules·hooks·MCP 설정은 SDK setting source에 포함하지 않습니다. 모델·context·Reasoning/Effort·Thinking·Fast는 선택한 exact SDK variant와 대조하며, outer Codex가 제공하지 않은 도구나 권한 요청은 실패로 종료합니다. 이 SDK 연동은 아직 실험 기능입니다.

Cursor SDK는 raw inference API가 아니라 agent workflow입니다. Cursor 항목의 요청 횟수와 비용은 로그인한 Cursor 구독 pool을 따르며, SyncBar의 OpenAI 계정 사용량 화면에는 합산되지 않습니다. 병합 선택기의 기존 Codex 항목은 로컬 브리지가 고정된 공식 OpenAI/ChatGPT endpoint로만 전달하며, Cursor 항목은 Cursor SDK로 보냅니다. 도구 루프는 Codex의 `previous_response_id` 또는 작업별 `prompt_cache_key`가 이어질 때 SDK agent를 재개하고, 브리지 재시작 뒤에도 private checkpoint에서 session identity와 동적 도구 상태를 복원합니다. SDK가 반환한 input·output·cache read·reasoning 사용량은 Responses usage로 전달합니다. `SYNCBAR_CURSOR_METRICS=1`을 설정하면 요청 내용이나 인증값 없이 준비 시간, 첫 텍스트 시간, Cursor 총시간, 입출력 바이트만 bridge stderr의 JSON line으로 기록합니다. 브리지는 Responses의 `function`·`custom`·`namespace`·동적 `tool_search` 도구 루프와 Codex가 만든 inline 이미지 입력을 지원합니다. `input_file.file_data`는 엄격한 Base64 검증 뒤 텍스트·코드·Markdown·HTML·CSV·JSON·XML, DOCX·PPTX·XLSX·ODT, 이미지, PDF를 처리합니다. Office 문서는 제한된 별도 프로세스에서 텍스트만 추출하고, PDF는 macOS의 PDFKit helper 또는 Windows의 self-contained WinRT/PdfPig helper로 텍스트와 최대 16장의 페이지 이미지를 함께 전달합니다. 원본 Base64와 임시 경로는 Cursor 프롬프트에 남기지 않습니다. 브리지 제한은 파일 8개, 파일당 12 MiB, 합계 24 MiB, 추출 텍스트 파일당 2 MiB·합계 4 MiB이며 PDF 페이지 이미지는 일반 이미지와 동일한 16장·24 MiB 한도를 공유합니다. 원격 Linux에는 PDF helper가 없으므로 PDF는 명시적으로 거절하지만 텍스트와 Office 문서는 처리할 수 있습니다.

Cursor 개인 플랜은 월간 사용량·남은 구독 pool을 반환하는 공개 API나 CLI 명령을 제공하지 않습니다. SyncBar는 사용자가 앱 안의 Cursor 페이지에 직접 로그인한 전용 WebKit 프로필로 공식 `cursor.com/dashboard/spending` 화면을 연 뒤, 그 페이지가 사용하는 `/api/usage-summary` 응답만 읽어 Cursor Models·Other Models 잔여량을 표시합니다. 비밀번호나 브라우저 쿠키를 외부로 내보내지 않으며, dashboard 계약이 바뀌면 사용량 조회가 안전하게 실패하고 다시 로그인을 요청합니다. Team/Enterprise의 Admin API는 별도 조직 관리자 키가 필요한 다른 인증 경계이므로 저장된 개인 User API Key로 호출하지 않습니다.

현재 Codex Desktop의 일반 파일 선택기는 파일 바이트를 Responses `input_file`로 보내지 않고 `## 이름: 절대경로` 텍스트와 UI용 attachment metadata를 전송합니다. 이 경우 Cursor가 파일을 직접 읽는 대신 제공된 Codex outer `exec`/파일 도구를 요청하고, 실제 읽기는 원래 프로젝트의 Codex sandbox·승인 경계 안에서 수행됩니다. 따라서 Cursor SDK에는 실제 프로젝트 workspace 읽기 권한을 열지 않습니다. 위 `file_data` 지원은 해당 wire를 보내는 Responses 클라이언트와 materialized attachment에 직접 적용됩니다. 보안 경계를 우회하는 `file://`·임의 로컬 경로 읽기, OpenAI Files 자격 증명이 필요한 `file_id`, 원격 `file_url`은 명시적으로 거절합니다.

HTML 그래프는 Codex의 outer tool이 visualization 파일을 만든 뒤 반환하는 inline directive를 byte-for-byte 보존합니다. Computer Use·브라우저·이미지 생성은 Codex의 outer custom/plugin tool이 실행하고, 그 결과 이미지나 스크린샷을 다음 Cursor turn에 전달하는 방식으로 동작합니다. Cursor ACP가 제공하지 않는 direct `computer_call`·provider-side `image_generation_call`과 오디오 입력은 지원하지 않으며 조용히 버리지 않고 오류로 종료합니다. `web_search`·`image_generation`·`tool_search`는 요청 전체를 실패시키지 않되 Cursor backend에는 사용할 수 없는 도구로 표시합니다. 변환된 프롬프트는 프로세스 인자가 아닌 stdin으로 전달합니다. `max`는 Codex의 모델 기능 설정에 따라 별도 활성화가 필요할 수 있습니다.

## 인증과 보안

- 전체 refresh token은 이 Mac의 권한 `0600` 인증 파일에만 보관합니다.
- SSH 장치에는 `refresh_token`을 비운 access-only 인증만 전달합니다.
- SSH 비밀번호와 개인 키 암호는 macOS Keychain의 기기 전용 항목에만 저장합니다.
- Cursor SDK 브라우저 로그인이 발급한 만료형 자격증명은 이 Mac의 기기 전용 Keychain에 저장합니다. SSH 동기화 후에는 원격 `~/.local/share/gpt-switch/cursor-remote-runtime.json`에도 소유자 전용 `0600` 파일로 보관되며, 원격 저장본은 macOS Keychain처럼 암호화된 파일이 아닙니다.
- 원격 Cursor SDK는 SyncBar 전용 `HOME`·XDG 상태 경로와 고정된 SDK runtime을 사용합니다. SDK account·model 확인과 `cursor_backend=sdk` health 검증 중 하나라도 실패하면 SyncBar도 안전하게 중단합니다.
- SDK 자격증명과 bridge token은 SSH 명령, 프로세스 인자, Codex 설정, 정상 stdout/stderr에 넣지 않습니다. 원격 manager는 저장된 자격증명을 Cursor SDK 자식 프로세스의 전용 환경으로만 주입합니다.
- 이 Mac의 Keychain 키만 삭제해도 이미 동기화된 원격 키는 남습니다. **이전 Codex 모델로 복구**로 원격을 함께 정리하거나, 실패한 장치가 다시 연결된 뒤 복구를 재시도해야 합니다.
- 개인 키와 인증서는 절대 경로의 일반 파일이어야 하며 심볼릭 링크를 허용하지 않습니다.
- 개인 키 소유자는 현재 사용자여야 하고 권한은 `0400` 또는 `0600`이어야 합니다.
- SSH는 strict host-key checking을 사용하고 agent, X11, 포트 포워딩과 TTY 할당을 차단합니다.
- 인증 파일 변경은 임시 파일 작성, 권한 검증, 원자적 교체 순서로 진행합니다.
- 계정 전환은 모든 장치의 사전 점검과 사후 검증을 통과해야 완료됩니다.

### Windows

- 전체 refresh token은 `%LOCALAPPDATA%\CodexSyncBar\profiles\<id>.auth.json`에만 보관하고, SSH 장치에는 `refresh_token`이 빈 access-only 인증만 전달합니다.
- SSH 비밀번호·개인 키 암호·Cursor User API Key는 Windows DPAPI로 암호화한 사용자 전용 비밀 저장소에 보관합니다. 원격으로 전달할 때는 SSH askpass 또는 stdin으로만 전달합니다.
- SSH 인증 파일과 설정 파일은 현재 Windows 사용자 소유·비공개 ACL인지 확인하고 재분석 지점(심볼릭 링크)을 거부하며, 저장은 임시 파일과 원자적 교체를 사용합니다. 새 장치는 검증 전까지 자동 동기화 대상이 아닙니다.
- 원격 부트스트랩은 변경 전 대상 helper·인증·상태를 암호화되지 않은 대신 사용자 전용 ACL로 보호된 로컬 복구 archive에 보관하고, 설치나 연결이 중단되면 같은 SSH endpoint에서 다음 실행 때 이전 상태를 복원합니다. endpoint가 바뀌면 자동 복구하지 않고 경고합니다.
- 계정 로그아웃은 계정 항목을 유지한 채 모든 활성 장치를 fallback으로 전환하고 원격·로컬 journal을 검증합니다. 계정 항목 삭제는 인증 파일이 없는 상태에서만 수행하며, 중단된 작업은 다음 실행 때 복구를 시도하고 복구 백업을 검증 전에는 삭제하지 않습니다.
- Codex 프로세스가 실행 중이면 백그라운드 인증 refresh를 미루어 실행 중인 클라이언트와 인증 파일이 경쟁하지 않게 합니다. 명시적인 계정 전환·로그아웃에서는 필요한 프로세스만 안전하게 재시작합니다.
- Cursor provider 설정은 변경 전 model/provider/catalog와 관리 블록을 기록하며, 외부에서 `config.toml`이 바뀌면 자동 덮어쓰기를 거부합니다.
- Windows publish에는 `Runtime/PdfExtractor/cursor-file-extractor.exe`가 self-contained helper로 함께 들어가며, 로컬 PDF의 텍스트·페이지 이미지를 macOS PDFKit 경로와 같은 Responses 입력 계약으로 처리합니다.

Chrome 쿠키와 Codex 토큰은 서로 다른 세션입니다. 앱 업데이트나 일시적인 네트워크 오류만으로 삭제되지는 않지만, OpenAI/Google에서 로그아웃했거나 보안 설정 변경·관리자 회수·refresh token 폐기가 발생하면 재로그인이 필요할 수 있습니다.

## 사용량과 비용 표시

- 계정 한도는 현재 선택한 계정의 Codex 호환 사용량 응답을 이용합니다.
- 장치 누적 사용량은 각 장치에 보존된 Codex 세션을 최근 30일 기준으로 집계합니다.
- 비용은 모델별 공개 API 가격과 캐시 입력 가격을 기준으로 계산한 **추정치**입니다.
- Fast/Priority 배율을 반영하지만, 공개 가격이 없는 모델은 임의의 가격을 만들지 않습니다.
- 이 값은 ChatGPT 구독 청구액이 아니며 OpenAI 사용량 대시보드와 완전히 같지 않을 수 있습니다.

## 문제가 생겼을 때

### 재로그인 안내가 반복되는 경우

설정의 계정 상태에서 **재로그인**을 선택하세요. 인증 파일이 존재하는 것만으로 refresh token의 유효성을 보장할 수 없으므로 앱은 실제 갱신 경로를 확인합니다.

### SSH 장치에 이전 계정이 남는 경우

1. 장치 상태를 새로고침합니다.
2. 설정의 장치 페이지에서 해당 장치를 다시 **설치 및 활성화**합니다.
3. helper 버전과 현재 계정 검증이 완료된 뒤 다시 전환합니다.

### `another controller operation is already running`이 표시되는 경우

다른 전환·갱신 작업이 끝날 때까지 잠시 기다린 뒤 새로고침하세요. 앱과 shell helper가 같은 잠금을 사용하므로 동시에 인증 파일을 변경하지 않습니다. 비정상 종료된 작업의 잠금은 소유 프로세스와 파일 권한을 확인한 뒤에만 자동 복구합니다.

### macOS Chrome 로그인이 원하는 계정으로 열리지 않는 경우

설정의 해당 계정에서 Chrome 세션을 로그아웃한 뒤 재로그인하세요. 계정별 Chrome 프로필은 분리되어 있으며 다른 계정의 쿠키를 삭제하지 않습니다.

## 소스에서 빌드하기

```bash
bash Tests/helper-contract-tests.sh
swift test
./build-app.sh
```

유니버설 바이너리로 패키징하려면 다음과 같이 실행합니다.

```bash
CODEX_SYNCBAR_UNIVERSAL=1 ./build-app.sh
```

기본 출력 위치는 이 저장소 기준 `../../outputs`입니다. 다른 위치를 사용하려면 첫 번째 인자로 경로를 전달하세요.

```bash
./build-app.sh "$PWD/release-assets"
```

빌드에는 앱과 동일한 `gpt-switch`, Keychain askpass bridge, 사용량 집계 helper가 포함됩니다. 앱 실행 시 다음 위치에 원자적으로 설치합니다.

- `~/.local/bin/gpt-switch`
- `~/.local/lib/gpt-switch/codex-syncbar-askpass`
- `~/.local/lib/gpt-switch/usage-summary.mjs`
- `~/.local/lib/gpt-switch/cursor-codex-bridge.mjs`
- `~/.local/lib/gpt-switch/cursor-file-extractor`
- `~/.local/lib/gpt-switch/cursor-remote-manager.mjs`

`v*` 태그 릴리즈는 다음 GitHub Actions secret이 모두 있어야 실행됩니다. P12와 App Store Connect API 개인 키는 각각 base64로 저장합니다.

- `APPLE_DEVELOPER_ID_P12_BASE64`
- `APPLE_DEVELOPER_ID_P12_PASSWORD`
- `APPLE_NOTARY_KEY_BASE64`
- `APPLE_NOTARY_KEY_ID`
- `APPLE_NOTARY_ISSUER_ID`

## 데이터 위치와 삭제

주요 데이터는 다음 위치에 있습니다.

- 설정과 계정 인증: `~/.local/share/gpt-switch`
- 앱 전용 Chrome 세션: `~/Library/Application Support/Codex SyncBar`
- SSH 비밀번호·키 암호: macOS Keychain 서비스 `com.sunggu.codexsyncbar.ssh`
- Cursor SDK 구독 자격증명: macOS Keychain 서비스 `com.sunggu.codexsyncbar.cursor`, account `sdk-subscription-credential-v1`
- SSH 원격 Cursor runtime·인증: 원격 `~/.local/share/gpt-switch/cursor-remote-runtime.json` 및 `cursor-remote-xdg/`

Windows에서는 설정과 계정 인증이 `%LOCALAPPDATA%\CodexSyncBar`, 활성 Codex 인증이 `%USERPROFILE%\.codex\auth.json`에 있습니다. 로그인 쿠키와 세션은 Windows 기본 브라우저가 관리하며, 이전 버전의 격리 Chrome 세션은 `%LOCALAPPDATA%\CodexSyncBar\ChromeProfiles`에 남을 수 있습니다. Windows DPAPI 비밀은 앱 상태 루트의 `secrets` 아래에 저장되며, 중단된 원격 부트스트랩 archive는 `remote-bootstrap-transactions` 아래에 보관됩니다. 원격 Cursor 인증은 SSH 장치의 `~/.local/share/gpt-switch` 아래에 별도로 보관됩니다.

앱만 제거하려면 `/Applications/Codex SyncBar.app`을 휴지통으로 옮기면 됩니다. 계정·Chrome 세션·Keychain 항목까지 지우려면 먼저 설정에서 장치와 계정을 정리한 뒤 위 데이터 디렉터리를 삭제하세요. 원격 장치의 파일은 Mac 앱을 삭제하는 것만으로 자동 삭제되지 않습니다.

## 개발 메모

사용량 응답 처리는 `UsageService.swift`, 장치별 최근 30일 집계는 `TokenUsageService.swift`에 분리되어 있습니다. 로그인은 설치된 공식 Codex CLI의 app-server를 사용하며, 브라우저에는 `auth.openai.com`의 HTTPS 인증 URL만 전달합니다.
