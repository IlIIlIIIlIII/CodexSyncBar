# Windows 앱과 위젯

Codex SyncBar Windows는 WinUI 3/.NET 10 앱과 Windows 위젯 보드용 COM 제공자로 구성됩니다. `main`의 계정·사용량·SSH 동작을 기준으로 만들었으며 Cursor 브리지 기능은 포함하지 않습니다.

## 요구 환경

- Windows 11 x64 또는 ARM64, Windows Web Experience Pack(위젯 보드)
- 로컬 개발 위젯 검증에는 Windows 개발자 모드가 필요합니다. [Microsoft 위젯 제공자 문서의 필수 조건](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-cs#prerequisites)을 따릅니다.
- .NET 10 SDK: 빌드할 때 필요하며 배포본은 런타임을 포함합니다.
- 공식 Codex CLI와 Google Chrome: 계정 로그인에 필요합니다. 아래 준비 스크립트는 검증한 독립 CLI 0.155.1을 설치합니다.
- WSL/SSH 대상의 bash, jq, node, tar와 Codex CLI. SSH 최초 연결에서는 앱에 표시된 호스트 키 지문을 확인해 등록하며 기존 OpenSSH 신뢰 설정도 사용합니다.

## 로그인 CLI 준비

Windows PowerShell에서 다음 명령을 한 번 실행합니다. x64와 ARM64를 자동으로 감지하며, 필요한 경우 `-Architecture ARM64`로 지정할 수 있습니다.

```powershell
./Scripts/Windows/setup-cli.ps1
```

[공식 Codex 0.155.1 릴리스](https://github.com/openai/codex/releases/tag/rust-v0.155.1)의 아키텍처별 ZIP을 받고, GitHub 릴리스 API의 SHA-256 및 검토한 고정 체크섬과 일치하는지 확인합니다. CLI와 공식 보조 실행 파일은 `%USERPROFILE%\.codex-syncbar\Tools`에 설치하며 시스템·사용자 PATH를 바꾸지 않습니다. 이미 받은 같은 공식 ZIP은 `-ArchivePath C:\downloads\codex-0.155.1.zip`으로 재사용할 수 있습니다. 재사용 파일도 체크섬 검증을 거칩니다.

SyncBar는 기존 npm `codex.cmd`를 우선 사용하고, 없으면 이 독립 CLI를 사용합니다. Codex 데스크톱 패키지 내부 `codex.exe`는 앱 외부에서 실행할 때 로그인·app-server 접근이 거부될 수 있으므로 독립 CLI보다 후순위입니다. 준비 스크립트는 로그인이나 인증 파일 변경을 수행하지 않습니다. 설치 후 앱의 **계정 추가/로그인 열기**에서 사용자 로그인을 진행합니다.

## 빌드와 설치

Windows PowerShell에서 저장소 루트를 기준으로 실행합니다.

```powershell
./Scripts/Windows/build.ps1 -Architecture x64 -Package
./Scripts/Windows/build.ps1 -Architecture ARM64 -Package
```

스크립트는 테스트를 먼저 실행하고, 아키텍처에 맞는 Node.js를 체크섬 검증 후 준비하고, 위젯과 앱을 패키징합니다. 출력은 `dist/windows`입니다. 기본 MSIX는 서명되지 않으므로 배포하려면 패키지 Publisher와 일치하는 서명 인증서의 thumbprint를 전달합니다. 개인 키는 저장소에 넣지 않습니다.

```powershell
./Scripts/Windows/build.ps1 -Architecture x64 -Package -CertificateThumbprint YOUR_THUMBPRINT
./Scripts/Windows/install.ps1 -PackagePath PATH_TO_SIGNED_MSIX
```

로컬 개발 인증서 신뢰가 필요한 경우 `-DevelopmentCertificate PATH_TO_CER -TrustDevelopmentCertificate`를 설치 명령에 추가합니다. 조직 정책이나 인증서 저장소 권한에 따른 설치 오류는 스크립트가 그대로 보고합니다.

WSL에서 개발할 때는 소스를 Windows 로컬 경로로 복사해 빌드합니다.

```bash
python3 Scripts/Windows/stage-from-wsl.py /mnt/c/Users/USERNAME/AppData/Local/CodexSyncBarBuild/src
```

Windows 빌드 도구는 Windows 경로에서 실행합니다. WSL의 Linux 실행 파일 및 환경 변수를 그대로 Windows 자식 프로세스에 전달하지 않습니다.

## 사용법

1. 앱에서 계정을 추가합니다. 계정마다 별도 Chrome 프로필이 열립니다. 계정 선택과 실제 적용은 별도 동작입니다.
2. WSL 설정에서 사용할 배포판을 선택하고 설치·검증을 완료합니다. Docker 내부 배포판은 목록에서 제외합니다.
3. SSH 장치를 추가하고 설치 및 활성화를 완료합니다. 실패한 장치는 전환 대상에 포함되지 않습니다.
4. 원하는 계정을 선택하고 모든 장치 적용을 누릅니다. 일부 장치가 실패하면 변경된 대상을 되돌리고 결과를 표시합니다.
5. Windows 위젯 보드에서 **Codex 사용량 및 계정**을 추가합니다. 중간·큰 크기에서 사용량을 확인하고 계정 선택 후 모든 장치에 적용할 수 있습니다.

개발자 모드를 켜고 설치한 뒤에도 추가 목록에 Codex가 없으면, 아래 명령으로 현재 사용자 세션의 위젯 호스트만 종료한 후 작업 표시줄에서 보드와 **+**를 다시 엽니다. 이 PC에서는 설치 전에 실행된 호스트가 이전 목록을 유지했고, 호스트를 다시 시작한 뒤 같은 설치본이 표시됐습니다. 명령은 위젯 데이터나 계정·설정을 삭제하지 않습니다.

```powershell
./Scripts/Windows/restart-widget-host.ps1
```

주기적 상태 확인은 정지된 WSL 배포판을 깨우지 않습니다. 명시적인 전환·설치는 선택한 배포판을 시작할 수 있습니다. 일반 Codex CLI 작업은 종료하지 않으며 인증을 캐시하는 해당 앱 서버만 재연결 대상입니다.

## SSH 연결과 CLI 관리

SSH 장치는 표시 이름·호스트·포트·사용자 이름을 입력하고 인증 방식을 선택합니다. 비밀번호 인증에서는 비밀번호만, 개인 키 인증에서는 키 파일과 선택적인 키 암호만 표시합니다. SSH 인증서는 고급 옵션에 있습니다. 기존 장치 ID는 유지하며 새 장치 ID는 자동 생성합니다. 저장된 비밀은 표시하지 않습니다.

처음 연결하는 서버는 SHA-256 호스트 키 지문을 확인하고 등록합니다. 기존 서버 키가 바뀌었으면 자동 등록하지 않습니다. 비활성 장치도 **연결 테스트**가 가능하며 **설치 및 활성화**를 완료하면 계정 동기화에 포함됩니다. 비밀번호 도우미는 패키지와 같은 해시인지 검증한 실행 파일을 사용자 전용 경로에 배치하므로 WindowsApps의 외부 프로세스 실행 제한을 피합니다.

연결된 장치 목록에서 장치를 선택한 뒤 **Codex CLI 관리**를 누르면 현재 버전·경로와 업데이트 결과를 볼 수 있습니다. Windows의 npm/SyncBar 관리 설치와 SSH·WSL의 지원되는 기존 설치 방식을 사용합니다. 정지된 WSL은 조회만으로 시작하지 않으며 업데이트 버튼을 누르면 시작합니다. 업데이트 및 버전 검증 후 해당 장치의 관리 대상 Codex 연결을 재연결하고, 완료되지 않으면 재연결 대기로 표시합니다.

**모든 기기 CLI 업데이트**는 등록된 Windows·SSH·WSL 기기를 병렬로 업데이트합니다. 버튼을 누르면 기기별 연결 확인, 업데이트·재연결 진행, 완료·실패·재연결 대기 상태와 이전/이후 버전이 표시됩니다. 한 기기의 실패는 나머지 작업을 중단하지 않으며 실패한 기기만 다시 시도할 수 있습니다. 호스트 키 확인이 필요한 SSH 기기와 지원하지 않는 설치는 이유를 표시하고 건너뜁니다. 정지된 WSL 배포판은 이 명시적인 일괄 업데이트 실행으로 시작될 수 있습니다.

## 저장과 복구

- Windows 설정·암호화된 계정 프로필·복구 journal: `%LOCALAPPDATA%\CodexSyncBar` (MSIX가 가상화한 앱 데이터 경로를 사용할 수 있음)
- Codex 활성 인증: `%USERPROFILE%\.codex\auth.json` 또는 명시적으로 설정한 `CODEX_HOME`
- Chrome 프로필·로그인 임시 홈: `%USERPROFILE%\.codex-syncbar`
- WSL 설정은 `wsl-devices.json`으로 분리하며 기존 계정·SSH 설정 형식을 유지합니다.

보관용 인증과 SSH 비밀은 현재 Windows 사용자의 DPAPI로 암호화합니다. Codex가 읽는 활성 인증 및 로그인 임시 인증은 전용 ACL을 적용한 호환 JSON입니다. WSL/SSH에는 refresh token을 제거한 인증만 전달합니다. 위젯에는 인증정보가 전달되지 않습니다.

계정 전환·로그아웃은 공통 잠금과 journal로 직렬화합니다. 복구가 확인되지 않은 작업은 성공으로 표시하지 않으며 해결 전 다음 변경을 차단합니다. 만료·오프라인 오류에서는 마지막 정상 사용량과 시각을 유지합니다.

## 개발 QA

`--demo`는 메모리상의 가상 계정과 별도 named pipe를 사용하는 화면 QA 모드입니다. 실제 인증 파일을 읽거나 쓰지 않습니다. README 관리 화면도 이 모드로 생성하며, 실제 관리 화면과 별도의 표현용 페이지이므로 데모의 비활성 버튼은 운영 화면의 동작 검증에 사용할 수 없습니다. 실제 위젯 보드에 데모 데이터를 표시할 때는 **복사한 빌드 디렉터리에서만** `build.ps1 -DemoWidgets`를 사용합니다. 일반 배포본은 이 옵션 없이 빌드합니다.

```powershell
CodexSyncBar.Windows.exe --demo
CodexSyncBar.Windows.exe --demo --background
CodexSyncBar.Windows.exe --readme-demo=popover --demo-theme=dark --readme-output=C:\existing-folder\windows-dashboard.png
```

실제 검증 결과와 실행하지 못한 항목은 [QA 보고서](QA.md)에 기록합니다. 위젯 구조와 COM 등록은 위젯 프로젝트의 README를 참고하세요.

WSL에서 Windows Computer Use가 `sandboxCwd is not a local file URI`로 실패하는 환경의 원인과 적용한 연결 수정은 [WSL Computer Use 진단](WSL-COMPUTER-USE.md)에 기록했습니다.
