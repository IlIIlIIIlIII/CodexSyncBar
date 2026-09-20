# Windows 구현 및 실행 QA — 2026-09-20

## README 정리와 커밋 전 검증 — 2026-09-20

- 루트 README를 Windows 설치·계정 전환·SSH/WSL·병렬 CLI 업데이트·주간 자동 메시지 중심으로 정리했다. 기존 macOS 안내는 `docs/macos/README.md`에 보존했다.
- 관리 화면은 메모리 전용 데모 페이지를 네이티브 렌더링한 예시이며 운영 화면의 기능 검사로 집계하지 않는다. 위젯 이미지는 기존 v12 실제 템플릿의 합성 데이터 렌더다. 생성된 이미지를 직접 열어 잘림과 개인정보 부재를 확인했고 README 상대 링크도 검사했다.
- x64 Release 빌드: 경고·오류 0건. 기본 Windows 임시 폴더에서 Core 테스트 **223 통과·2 제외**. 임시 폴더 권한에 의존하던 테스트 인증·키 파일을 사용자 전용 권한으로 생성하도록 수정했다. 제품의 권한 검증은 유지한다.
- WSL의 Linux 파일시스템에 소스 사본을 두고 helper 계약, 원격 CLI 업데이트, 사용량 집계·동시 실행 회귀 검사를 통과했다. Windows 마운트의 파일 권한과 Linux 파일 권한을 혼동하지 않도록 분리했다. WSL 브리지 테스트 **12 통과**.
- CI에서 존재하지 않는 `remote-codex-update-tests.mjs`를 호출하던 부분을 실제 `.sh` 검사로 수정했다. 이 문서 변경에서 실제 계정 전송·원격 CLI 업그레이드·설치 앱 교체는 추가로 실행하지 않았다. macOS Swift 테스트와 ARM64 하드웨어 실행은 이번 로컬 검증에 포함하지 않는다.

## 주간 자동 메시지 연결·검증 — 1.0.0.20

- 기준은 `Sources/CodexSyncBar/WeeklyUsageAnchor.swift`의 맥 구현이다. 계정별 opt-in, 주간 잔여량 99.5% 기준, 첫 opt-in의 미사용 계정 즉시 전송, 2분 넘는 초기화 시각 이동의 2회 관찰, 30분 실패 재시도, 초기화 시각이 없는 성공 후 6시간 유예를 유지한다. 모델 `gpt-5.6-luna`, effort `low`, 동일한 한국어 확인 프롬프트를 사용한다. 5시간 한도는 대상이 아니다.
- 기존 Windows에도 설정과 전송 코드가 있었지만 UI 갱신 이벤트에 연결되어 있었다. 이를 공통 `SyncBarController`의 성공한 사용량 응답 경로로 옮겼다. 저장된 오프라인 캐시·누락된 주간 한도·재로그인 계정에서는 전송하지 않는다. 전송 직전과 완료 기록은 변경 잠금 안에서 원자적으로 저장하며, 수동·자동·별도 제어기 인스턴스가 같은 기록을 사용한다.
- 실행 공간을 가상화될 수 있는 Temp에서 사용자 전용 외부 런타임으로 옮기고, 디렉터리 ACL을 적용했다. refresh token은 전달하지 않는다. 원시 CLI 진단의 토큰을 제거하며, 완료 응답 파일이 없는 종료는 성공으로 취급하지 않는다. CLI의 격리 옵션은 [공식 비대화형 실행 문서](https://learn.chatgpt.com/docs/non-interactive-mode)와 설치된 CLI 도움말로 확인했다.
- Core **223 통과·2 제외**. 실제 네트워크 없이 초기화 전송·앱 재시작·동시 수동/자동 요청·다중 제어기 잠금·실패 재시도·초기화 이동·전송 임시 파일 권한/정리·비밀 미노출·응답 누락·화면 없는 제어기 전송·오프라인 캐시 차단을 검증했다.
- x64 `1.0.0.19`의 실제 설정 화면에서 메인 계정의 **지금 메시지 보내기**를 눌러, 2026-09-20 23:26 KST 완료와 오류 없음·성공 시각 저장·임시 런타임 제거를 확인했다. 실제 요청은 1회다. 최초 클릭은 시작 시점의 중복 평가로 전송되지 않았으며 시도 기록도 없었다. `1.0.0.20`은 꺼진 계정의 평가를 미리 제외하고 ‘전송 중’ 상태를 실제 전송 단계에만 표시하도록 보완했다.
- 자동 전송은 맥과 같이 계정별 기본 꺼짐을 유지한다. 실제 주간 초기화까지 기다린 검사는 아니며, 초기화·재시작·중복·재시도는 합성 시계/발송기로 검증했다. 사용량 카드의 바로가기와 설정 화면의 최근 전송 표시를 네이티브 창에서 확인했다.
- 최종 x64 `1.0.0.20` 업데이트 설치 및 x64·ARM64 서명 패키지 빌드를 완료했다. [실제 전송](qa-evidence/weekly-anchor-v20/real-send.json), [패키지·위젯 등록](qa-evidence/weekly-anchor-v20/package.json), [서명](qa-evidence/weekly-anchor-v20/signatures.json), [설치 후 상태](qa-evidence/weekly-anchor-v20/dashboard.json)를 기록했다. 계정 6개·최신 사용량 6개·연결된 장치 4/4·오류 없음이다. ARM64 하드웨어 실행은 미검증이다. 빌드 로그는 `dist/windows/weekly-anchor-v20-build.log`, `dist/windows/weekly-anchor-v20-arm64-build.log`다.

## SSH 활성화 파일 경로 수정 — 1.0.0.18

- `rogally` 활성화의 최초 실패는 OS가 관리하는 설치 패키지 연결 경로를 개인 상태 파일 검사로 거부한 것이었다. 복구 과정에서는 MSIX가 가상화한 AppData의 archive 경로를 외부 `scp.exe`가 찾지 못해 원래 오류까지 가려졌다.
- 앱에 포함된 세 helper만 신뢰하는 Runtime 경계 안에서 검사한다. Runtime 내부의 링크와 경계 밖 경로는 계속 거부하고, 개인 상태 파일의 전체 상위 경로 검사는 유지한다. SSH 업로드는 앱이 읽은 바이트를 AppData 밖 사용자 전용 임시 파일로 작성한 뒤 전송하며 성공·실패 시 삭제한다. 원본 복구 journal은 복구 성공 전까지 유지한다.
- Windows Core **213 통과·2 제외**. 패키지 루트 연결 허용, Runtime 내부 링크·경로 이탈 거부, 비공개 상태 검사 유지, 업로드 바이트 보존·ACL·실패 후 정리·동시 파일 분리를 검증했다. `Tests/remote-codex-update-tests.sh`도 통과했다.
- 실제 `rogally`의 미완료 복구 journal을 저장된 인증으로 복원한 뒤 ManualQa에서 설치·활성화·적용 계정 일치를 확인했다. 이어 네이티브 `1.0.0.18` 앱의 **설치 및 활성화** 버튼으로 재검증하여 연결됨 상태를 확인했다. [활성화 결과](qa-evidence/ssh-upload-v18/activation.json), [읽기 전용 상태](qa-evidence/ssh-upload-v18/dashboard.json)는 계정 6개·최신 사용량 6개와 Windows·ml·rogally 3/3 연결을 기록한다. `laptop` 활성화는 이번 검사 범위가 아니다.
- x64·ARM64 MSIX 빌드와 [서명 검사](qa-evidence/ssh-upload-v18/signatures.json)를 통과했고 x64 `1.0.0.18`을 적용했다. [패키지 검사](qa-evidence/ssh-upload-v18/package.json)는 payload·위젯 확장·COM class factory 정상이다. ARM64 하드웨어 실행 및 위젯 보드의 실제 버튼 클릭 검사는 수행하지 않았다. 빌드 로그: `dist/windows/ssh-upload-v18-build.log`, `dist/windows/ssh-upload-v18-arm64-build.log`.

## 병렬 일괄 CLI 업데이트 — 1.0.0.16

- **모든 기기 CLI 업데이트** 버튼으로 등록된 Windows·SSH·WSL을 병렬 실행한다. 기기별 확인 중·업데이트 중·완료·실패·재연결 대기·확인 필요 상태와 결과를 표시하며, 실패 기기만 재시도한다.
- 배치 전체에서 변경 잠금을 한 번 획득하고 각 기기의 작업은 별도 task로 실행한다. 신뢰되지 않은 서버나 미지원 설치를 건너뛰며 실패가 다른 기기를 취소하지 않는다.
- 코어 테스트 **209 통과·2 제외**. 두 기기가 동시에 업데이트에 진입한 상태에서 세 번째 기기의 실패가 즉시 보고되고, 나머지 기기는 완료·재연결 대기로 끝나는 것을 동기화 장벽으로 검증했다. 미신뢰·미지원 대상의 업데이트 미실행 및 중복 기기 거부도 검증했다.
- x64·ARM64 서명 패키지를 빌드하고 x64 `1.0.0.16`을 설치했다. [상태 조회](qa-evidence/cli-batch-v16/dashboard.json)는 계정 6개, 최신 사용량 6개, 기기 2/2 연결이다. [패키지 검사](qa-evidence/cli-batch-v16/package.json)는 payload·위젯 확장 및 COM class factory를 확인했다.
- 실제 설치된 CLI들을 일괄 업그레이드하는 작업은 실행하지 않았다. 병렬 처리와 상태 전파는 격리된 가짜 설치 서비스로 검증했다. 빌드 로그는 `dist/windows/cli-batch-build.log`, `dist/windows/cli-batch-arm64-build.log`에 있다.

## SSH·CLI 관리 수정 — 1.0.0.15

- x64 설치본 `1.0.0.15` 적용 및 x64·ARM64 서명 패키지 빌드 통과. 코어 테스트 **206 통과·2 제외**. 제외 항목은 별도 opt-in WSL 통합 테스트와 관리자 소유자 테스트다.
- 인증 방식별 화면을 네이티브 창에서 확인했다. 비밀번호 선택 시 키 경로가 숨겨지고, 개인 키 선택 시 키 파일·키 암호·고급 인증서 옵션이 표시된다. 장치 ID는 자동 생성하고 사용자 이름은 별도로 유지한다.
- 실제 `ml`에서 비밀번호 인증, helper 설치·활성화, 활성 계정 일치를 확인했다. 최초 호스트 키 미등록, Windows ssh-keyscan의 KEX 오류, 원격 설치 스크립트의 CRLF/줄바꿈 오류, WindowsApps 내부 askpass 실행 거부를 재현하고 수정했다.
- 최종 설치본의 [dashboard 상태](qa-evidence/ssh-cli-v15/dashboard.json)는 계정 6개 모두 최신 사용량 보유, Windows·SSH 장치 2/2 연결, 오류 없음이다. 원격 토큰 집계도 장치 2/2로 표시됐다.
- [패키지 검사](qa-evidence/ssh-cli-v15/package.json)는 askpass 포함 payload, 위젯 확장 등록과 COM class factory 활성화를 통과했다. 실제 위젯 보드 화면은 이번 변경에서 재검증하지 않았다.
- 설치본의 `Codex CLI 관리 · ml`에서 버전 `0.154.0`, 설치 경로 및 업데이트 버튼을 확인했다. Windows 관리 CLI 조회는 `0.155.1`을 반환했다. CLI 자체를 실제로 업그레이드하거나 실행 중인 사용자 세션을 재시작하지는 않았다.
- `Tests/remote-codex-update-tests.sh`는 버전 조회, 업데이트 성공·실패, 버전 검증 실패 시 프로세스 유지, 재연결 성공·대기, 미지원 설치 및 SSH 배치 회귀를 통과했다. Windows 테스트는 변조된 다운로드가 기존 CLI 선택을 바꾸지 못하는 경우와 WSL 조회가 정지된 배포판을 시작하지 않는 경우를 포함한다.
- 빌드 로그는 `dist/windows/ssh-cli-build.log`, `dist/windows/ssh-cli-arm64-build.log`에 있다. `mspdbcmf.exe` 부재로 심볼 패키지 생성 경고만 발생했으며 앱 패키지 빌드는 성공했다.

## 판정

소스, WinUI 실행 파일, 별도 COM 위젯 제공자, 서명한 x64·ARM64 MSIX를 만들었다. 네이티브 앱의 격리된 데모 실행과 실제 사용자가 진행한 OAuth 로그인·사용량 조회를 검증했고, 설치본 `1.0.0.5`는 실제 Windows 위젯 보드에서 중간 카드의 사용량 표시까지 확인했다. **위젯의 실제 계정 전환과 혼합 장치 복구를 포함한 완료 기준은 아직 충족하지 않았다.** 아래 미실행 항목을 통과로 집계하지 않는다.

이전 설치본은 **`1.0.0.12`**이다. 관리 창의 적용·보조 버튼 행을 고정하고, 앱과 모든 위젯 크기에 상대 초기화 시간과 `MM.dd (토) HH:mm` 날짜를 함께 표시한다. 큰 위젯은 사용량·초기화권·장치 상태를 구분하고 앱 열기를 상단으로 옮겼다. Windows PC 1대처럼 수량을 명시한다. Windows 테스트 190 통과·2 제외, 위젯 합성 렌더 72/72, x64·ARM64 빌드와 x64 설치·실제 상태 조회를 확인했다. **v12 실제 버튼·위젯 화면 QA는 WSL 화면 도구 오류로 미실행**이다. [화면 설계 기준](UI-DESIGN.md)을 함께 참고한다.

## 기준과 환경

- 기반: `main`의 `129be19`, 작업 브랜치 `codex/windows-winui-widgets`.
- 이전 Windows 브랜치의 Codex 코드만 선별해서 가져왔다. Cursor 기능은 포함하지 않는다.
- C#/.NET SDK 10.0.401, Windows App SDK 2.5.1 stable, WinUI 3/XAML, Widgets COM/Adaptive Cards.
- 작업 환경: WSL Ubuntu-26.04. 네이티브 실행 환경: Windows 11 Pro x64, OS build 26200.
- Windows 로컬 스테이징: `%LOCALAPPDATA%\CodexSyncBarBuild\package-check`.
- 계정 로그인: 공식 Codex CLI 0.155.1 standalone, 사용자 전용 Chrome 프로필. CLI 배포물은 공식 GitHub release SHA-256과 대조했다.
- Microsoft 참고 문서: [WinUI 3](https://learn.microsoft.com/ko-kr/windows/apps/winui/winui3/), [Windows App SDK 릴리스](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels), [C# 위젯 제공자](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-cs).

## 확인한 기능

| 범위 | 구현 및 검증 근거 |
| --- | --- |
| 공통 제어 | Core의 `SyncBarController`, `DashboardSnapshot`, `IAccountTarget`, `SwitchOperation`을 트레이·관리 창·위젯 IPC가 공유한다. |
| 전환·복구 | 전체 사전 점검, 원격 먼저·Windows 마지막 적용, 전체 검증, 역순 복구, 실패한 복구 대상 보존. 중단된 전환·로그아웃·부트스트랩의 journal 복구, 재로그인 뒤 과거 로그아웃 재적용 방지, 전환 이전/늦게 끝난 장치 조회의 덮어쓰기 방지를 테스트했다. |
| 인증 저장 | Windows 보관본/복구 사본은 현재 사용자 DPAPI, 활성 auth는 사용자 전용 ACL과 원자적 교체. LocalService 읽기 권한 거부를 실제 Windows에서 검증했다. 외부 CLI 로그인 홈은 별도 경로다. |
| 로그인·갱신 | 로그인 ID가 다른/누락된/잘못된 완료 이벤트, 취소, 중복 계정을 합성 app-server 통신으로 검사했다. 갱신 실패 뒤 회전한 토큰 보존과 중단 후 복구를 검사했다. |
| 앱 서버 재연결 | 명시된 소켓 경로가 현재 Codex 홈에 속하는 서버만 재연결 대상으로 삼는다. 다른 홈·알 수 없는 경로·일반 CLI를 구분하는 20개 파서 사례를 검사했다. 실제 실행 중인 프로세스 종료 검사는 하지 않았다. |
| WSL | Docker 배포판 제외, 설치 성공 후 활성화, 정지된 배포판의 주기적 조회에서는 시작하지 않음, 명시적 적용/복구에서만 시작 가능. access-only 인증과 0600 파일을 실제 POSIX helper로 검증했다. |
| SSH | 기존 main helper 2.1.3, usage schema 6과 호환. strict host-key 검사, CLI 업데이트, access-only 인증·저장된 비밀·전환 복구 코드를 연결했다. 실제 장치 검증 범위는 아래를 참고한다. |
| 사용량 | 현재 모델·응답 파서·표시 설정은 5시간·주간 한도만 제공한다. 초기화권 수량·만료, 마지막 정상 값/시각, 401·오프라인·누락값·주간만 있는 응답, 오래된 세대의 응답 차단은 유지한다. 이전 응답·캐시의 Spark 필드는 무시하며 지원하는 한도 값은 보존한다. 후속 변경의 focused 회귀 결과는 아래 별도 표에 기록했다. |
| 토큰 집계 | main의 최근 30일 집계와 가격/긴 컨텍스트/캐시 쓰기 형식, 알 수 없는 모델의 미가격 처리를 이식했다. 과거 Spark 모델의 토큰 버킷과 기존 가격 지원 여부 처리는 보존한다. 한도 화면에서 삭제한 기능 때문에 이미 사용한 토큰의 집계까지 사라지지 않도록 하기 위함이다. |
| 위젯 | 중간·큰 크기 카드, 적용 계정과 선택 계정 구분, Apply/Refresh, 오래된 구성/삭제된 계정/중복 요청 차단, 제공자 재시작 후 작업 조회를 구현했다. 새 디자인은 제공된 한도 수치와 갱신 시각에 집중하고, 큰 카드에 초기화 시각·초기화권 만료 상세·장치 요약을 추가한다. 긴 문구를 제한해 버튼 공간을 확보한다. v8 실제 보드 표시는 아직 미확인이다. |

## 실행 결과와 증거

다음 표의 전체 Core 테스트 수치와 설치본 화면 결과는 **디자인 변경 전 실행 기록**이다. `1.0.0.5`로 명시된 위젯 보드 결과는 실제 보드 관찰이며, 데모·렌더러 결과와 구분한다.

| 검사 | 결과 | 근거 및 한계 |
| --- | --- | --- |
| Windows x64 WinUI·위젯 빌드 | 통과 | 네이티브 Windows 빌드. |
| Windows ARM64 빌드·MSIX | 통과 | x64 Windows에서 교차 빌드·패키징. ARM64 하드웨어 실행은 미실행. |
| 서명된 MSIX 파일 구조 | 통과 | 앱·위젯 실행 파일/런타임, Node, helper, COM 등록, 위젯 정의, 이미지, 패키지 서명을 검사했다. |
| Linux Core 자동 테스트 | 135 통과 / 9 제외 / 144개 | [실행 로그](qa-evidence/core-linux-tests.txt). Windows 전용 검사는 명시적으로 skip한다. |
| Windows Core 자동 테스트 | 142 통과 / 2 제외 / 144개 | [실행 로그](qa-evidence/core-windows-tests.txt). POSIX helper 통합과 관리자 토큰이 필요한 검사만 제외했다. 공식 CLI, DPAPI, ACL, cmd 인자·환경·stdin 검사를 실제 Windows에서 실행했다. |
| shell helper 회귀 | 통과 | `Tests/helper-contract-tests.sh`. GNU/BSD stat 차이를 수정해 Linux에서도 실제 실행했다. |
| Node 사용량·원격 CLI 업데이트 회귀 | 통과 | `Tests/usage-summary-tests.mjs`, `Tests/remote-codex-update-tests.mjs`. |
| 실제 loopback SSH + helper | 17개 검사 통과 | [JSON 결과](qa-evidence/ssh-loopback-smoke.json). 사용자 권한 OpenSSH 서버와 생성한 키, 고정한 호스트 키, 강제한 임시 HOME을 사용했다. bootstrap·access-only 전달·전환·검증·복구·0600/잘못된 권한/refresh token 거부를 검사했다. C# 어댑터와 실제 장치를 함께 쓴 혼합 전환 검사는 별도다. |
| 실제 Windows → WSL 검색 | 통과 | [JSON 결과](qa-evidence/wsl-native-discovery.json). 네이티브 `WslDeviceService`가 `Ubuntu-26.04`를 실행 중인 후보로 반환했다. 목록 조회만 수행하고 설정·인증·배포판 시작 상태는 변경하지 않았다. |
| 네이티브 데모 IPC | 10/10 통과 | [JSON 결과](qa-evidence/demo-ipc-results.json). 백그라운드 시작, 단일 인스턴스, 새로고침, stale/중복/동시 요청, 전환 완료, 설정 열기. 장치들은 가상이다. |
| 사용자 수동 OAuth + 실제 API | 6/6 통과 | [JSON 결과](qa-evidence/manual-login-results.json). 공식 CLI 로그인/계정 확인, DPAPI 보관, 활성 파일 미변경, refresh token 제거, 사용량 API와 한도 응답 확인. 비밀·이메일은 보고서에 넣지 않았다. |
| WinUI 밝은/어두운 화면 | 렌더링 확인 | [화면 증거와 재현 방법](qa-evidence/README.md). 밝은 트레이의 흰색 글자 문제를 발견·수정했다. OS 배율/키보드/접근성 검사와 구분한다. |
| MSIX 설치·COM 활성화 | 통과 (`1.0.0.5`) | 사용자가 개발 인증서를 LocalMachine TrustedPeople에 등록해 초기 `0x800B0109`를 해결했다. 검증한 x64 설치본은 `1.0.0.5`, Status `Ok`, 개발자 모드 값 `1`이다. 설치본의 위젯/COM/StartupTask와 `CoGetClassObject`·`CoCreateInstance`를 검사해 실제 제공자 생성 및 `GetWidgetInfos` 초기화를 확인했다. 고정된 위젯이 없을 때 API가 null을 반환하는 시작 오류를 수정했다. [설치 결과](qa-evidence/installed-package.json). 실제 보드 표시 결과는 아래 별도 행에 기록했다. |
| 패키지 업데이트·실제 공통 상태 | 통과 | 제거 없이 `1.0.0.5`으로 업데이트한 뒤 기존 계정 1개·적용 계정·Windows 장치와 5분 이내 주간 사용량이 보존됐다. 생산용 `GetSnapshot`에서 로그인 필요/작업 중/오류가 없음을 확인했다. [비식별 조회 결과](qa-evidence/production-ipc-after-update.json). 위젯 보드의 재연결은 별도 미실행이다. |
| 설치본 관리 화면·백그라운드 재열기 | 부분 통과 | 같은 WSL 작업의 공식 computer-use로 실제 `1.0.0.5` 창 실행/최대화/새로고침/WSL 관리/닫기/재열기를 확인했다. DPI 기본 크기, 가로 잘림, MSIX 토큰 캐시 EXDEV를 수정한 뒤 실제 화면에서 해소를 확인했다. 관리 창을 닫아도 IPC가 유지되고 재열기 뒤 계정·사용량이 남는다. [검증 기록](qa-evidence/installed-app-ui-qa.json). 스크린리더·전체 배율·위젯 보드 통과를 뜻하지 않는다. |
| OS 위젯 확장 등록·실제 추가 목록 | 등록·재시작 후 목록 표시 통과 | `AppExtensionCatalog.FindAllAsync`가 설치본 `1.0.0.5`의 `CodexSyncBar.Widgets`를 반환했고, 정의·COM·이미지·Public 폴더를 읽었다. 처음 실제 추가 목록에는 Codex가 없었으나, 같은 패키지를 유지한 채 WidgetService·WidgetBoard를 재시작한 뒤 공식 computer-use 화면에서 등장했다. OS 등록, 목록 표시와 고정·상호작용은 별도 판정이다. [비식별 감사 결과](qa-evidence/widget-extension-registration.json). |
| 실제 위젯 보드 중간 카드 표시 | 통과 (`1.0.0.5`) | 사용자가 추가 버튼을 누른 뒤 실제 보드에서 가린 계정명, 실제 주간 사용량, 5시간 정보 없음, 갱신 시각, 계정 선택과 적용·새로고침 버튼이 잘리지 않고 표시됨을 공식 computer-use 화면으로 확인했다. [실제 보드 기록](qa-evidence/widgets-board-ui-qa.json). 미리보기 PNG와 구분한다. |
| 실제 위젯 크기 변경·버튼·전환 | 미실행 | 버튼이 보이는 것과 실행 결과를 구분한다. 실제 새로고침·큰 크기·다중 계정/장치 전환·업데이트 후 재연결은 아직 검증하지 못했다. |
| 중간 카드 긴 문구 회귀 | 통과 | 별칭 70자·오프라인 문구 180자를 실제 Core 카드로 렌더했다. 기본 14px, small/default 간격 8/12px에서 높이 529.6px의 넘침을 재현하고 280.6px(한도 291px)로 수정했다. 앱·새로고침·적용 버튼 모두 보이며 관련 카드/IPC 테스트 23개가 통과했다. [렌더 결과](qa-evidence/widget-preview-render.json). 실제 보드 DPI 검사는 별도다. |
| macOS Swift 회귀 | 로컬 미실행 | 이 호스트는 WSL/Windows. CI macOS job을 추가했으나 원격 CI는 실행하지 않았다. |

## v7 디자인·한도 정리 검증

| 검사 | 현재 결과 | 근거 및 남은 확인 |
| --- | --- | --- |
| Linux Core 자동 테스트 | 148 통과 / 9 제외 / 157개 | [v7 실행 로그](qa-evidence/design-v7/core-linux-tests.txt). Windows 전용 검사 9개를 제외했다. 아래 focused 검사와 위젯 계약 검사가 포함되므로 통과 수를 더해서 집계하지 않는다. |
| Windows Core 자동 테스트 | 155 통과 / 2 제외 / 157개 | [v7 빌드·테스트 로그](qa-evidence/design-v7/build-x64.log). POSIX helper 통합과 관리자 토큰이 필요한 검사만 제외했다. |
| Spark 한도 제거·설정 마이그레이션·사용량 회귀 | focused 19/19 통과 | 전체 실행에 앞서 `UsagePreferencesMigrationTests`, `ControllerUsageTests`와 관련 기존 사용량/트레이 제목 검사를 Linux에서 실행했다. 상속 `CODEX_HOME`·상태 환경 변수를 제거하고 각 fixture의 명시적 임시 홈을 확인했다. |
| 이전 설정·캐시 호환 | 위 focused 검사에 포함 | Spark 키만 제거하고 기존 5시간/주간 선택, 순서와 알 수 없는 추가 설정은 보존한다. Spark 전용 선택에는 5시간·주간 기본값을 넣고, 사용자가 명시한 빈 선택은 유지한다. 이전 대시보드 캐시의 지원 한도·초기화권과 과거 Spark 토큰 버킷도 보존한다. |
| 트레이 WinUI 리소스 | 정적 확인 | 현재 SDK의 리소스 키 존재를 확인했다. 테마·DPI·스크린리더의 실제 동작 통과를 뜻하지 않는다. |
| 위젯 계약 테스트 | 25/25 통과 | [v7 결과](qa-evidence/widget-design-v7/results.json). 카드의 계정 선택·동작·상태·길이 제한 검사이며, 실제 보드의 버튼 실행 결과와 구분한다. |
| 위젯 중간·큰 카드 렌더 | 24/24 통과 | [v7 렌더 결과와 화면](qa-evidence/widget-design-v7/results.json). 두 크기 × 밝은/어두운 테마 × 정상·주간만 있음·긴 문구·계정 없음·작업 중·오류의 6개 상태를 합성 데이터로 검사했다. Adaptive Cards JS 렌더러에서 영역과 버튼 잘림을 검사했으며 실제 보드·OS 배율 검사가 아니다. |
| Windows x64 v7 빌드·MSIX 생성 | 통과 | [v7 빌드 로그](qa-evidence/design-v7/build-x64.log). `1.0.0.7` x64 MSIX 생성, 오류 0개. `mspdbcmf.exe` 부재로 심볼 패키지를 생성하지 못한다는 경고 1개가 남는다. |
| Windows ARM64 v7 빌드·MSIX 생성 | 통과 | [v7 빌드 로그](qa-evidence/design-v7/build-arm64.log). ARM64 하드웨어 실행은 미실행. |
| v7 MSIX 설치·업데이트 | 통과 | 아래 설치·상태 보존 검사와 증거를 참고한다. |
| v7 관리 창·트레이 화면 | 부분 확인 | 실제 관리 창의 어두운 테마와 설정·F5, 격리된 트레이 양 테마 렌더를 확인했다. 좁은 창·전체 배율·고대비는 미검증이다. |
| v7 위젯 중간·큰 카드 | 대기 | 새 카드의 실제 보드 표시·크기 변경·새로고침·적용 버튼과 패키지 업데이트 후 연결 유지 여부를 확인해야 한다. 이전 v5 표시와 로컬 미리보기 렌더링으로 대체하지 않는다. |

## v7 설치와 화면 확인

| 검사 | 결과 | 증거와 범위 |
| --- | --- | --- |
| x64·ARM64 서명 MSIX | 통과 | [패키지 검사](qa-evidence/design-v7/package-artifacts.json), [ARM64 빌드](qa-evidence/design-v7/build-arm64.log). ZIP CRC, 실행 파일 아키텍처, COM·위젯 정의, 이미지, Authenticode Valid. ARM64 하드웨어 실행은 제외한다. |
| x64 업데이트 설치·COM·상태 보존 | 통과 | [설치/등록](qa-evidence/design-v7/installed-package.json), [비식별 IPC](qa-evidence/design-v7/production-ipc.json). 버전 1.0.0.7 Status Ok, 기존 계정·적용 계정·Windows 장치와 최신 주간 사용량 유지. |
| 실제 관리 화면·설정·키보드 새로고침 | 통과한 범위 명시 | 공식 computer-use로 새 관리 창, 선택/적용 계정 구분, 주간 잔여량, 5시간 정보 없음, Spark 없는 설정을 관찰했다. F5 뒤 갱신 시각과 사용량 변경을 확인했다. [화면 검증 기록](qa-evidence/design-v7/installed-ui.json). 창 크기 변경 시도는 실제 크기를 바꾸지 못해 compact 검증으로 집계하지 않았다. |
| 트레이 밝은/어두운 렌더 | 통과한 범위 명시 | 설치본의 격리된 메모리 데모를 네이티브 렌더했다. [밝은 화면](qa-evidence/design-v7/tray-light.png), [어두운 화면](qa-evidence/design-v7/tray-dark.png). 텍스트·선택 상태·고정 적용 버튼을 확인했다. 진행률은 초기 애니메이션 중일 수 있으며 실제 Explorer·가장자리·배율 검사를 대체하지 않는다. |
| 새 디자인 실제 위젯 보드 | 미확인 | 현재 공식 도구의 창 목록에 보드가 없고 Explorer 관련 캡처에도 표시되지 않았다. 24개 카드 렌더와 OS COM/등록 검사는 통과했지만 실제 보드 QA로 집계하지 않는다. |

## v8 반응형 배치·초기화권 검증

v8은 상세 영역 전체 너비 사용, 실제 내용 너비 1000 DIP에서 카드 두 열 배치, 수치 영역 520 DIP에서 한도 병렬 배치, 제목 옆 별칭 편집을 구현한다. 5시간 한도 누락 시 해당 영역을 숨기되 사용자 표시 설정은 유지한다. 초기화권은 전용 카드에서 수량과 만료 그룹을 구분하고, 한국어 전체 요일을 포함한 로컬 시각과 일·시간·분 단위 남은 시간을 제공한다. 위젯은 5시간 녹색·주간 파란색 계열과 경고 의미 색을 쓰며 초기화권 상세는 큰 크기에 배치한다. 실제 화면과 합성 데이터 검증 범위는 아래에 구분했다.

| 검사 | 현재 결과 | 근거 및 범위 |
| --- | --- | --- |
| 초기화권 공통 포맷터 | focused 22/22 통과 | [포맷터](../../Windows/CodexSyncBar.Windows.Core/UsageFormatting.cs), [검사](../../Windows/CodexSyncBar.Windows.Core.Tests/ResetCreditExpiryFormattingTests.cs). null/빈 목록, 동일 실제 시각의 중복 수량·정렬, 24시간·분 경계, 1분 미만·만료, 긴 기간의 분 계산, en-US/ar-SA/th-TH에서 한국어 전체 요일·그레고리력 표기와 로컬 시간 변환, 기존 간략 표시 호환을 검사했다. 인증·실제 사용자 데이터에 접근하지 않는 순수 포맷터 검사다. |
| Linux 전체 Core 자동 테스트 | 177 통과 / 9 제외 / 186개 | [v8 실행 로그](qa-evidence/design-v8/core-linux-tests.txt). Windows 전용 검사 9개를 제외했으며 focused 검사는 전체 수에 포함되므로 더해서 집계하지 않는다. |
| Windows 전체 Core 자동 테스트 | 184 통과 / 2 제외 / 186개 | [v8 실행·빌드 로그](qa-evidence/design-v8/build-x64.log). POSIX helper 통합과 관리자 토큰이 필요한 검사만 제외했다. |
| 위젯 계약·렌더 | 계약 31/31, 렌더 36/36 통과 | [v8 결과와 화면](qa-evidence/widget-design-v8/results.json). 중간·큰 크기 × 밝은·어두운 테마 × 정상·긴 문구·계정 없음·작업 중·오류·주간만 있음·다중 만료·0개·미확인의 9개 상태다. 합성 렌더의 최대 내용 하단은 중간 293/304px, 큰 크기 604/620px이며 실제 보드 결과가 아니다. |
| x64·ARM64 빌드·서명 MSIX | 통과 | [x64 빌드](qa-evidence/design-v8/build-x64.log), [ARM64 빌드](qa-evidence/design-v8/build-arm64.log), [패키지 검사](qa-evidence/design-v8/package-artifacts.json). ZIP CRC, 필수 실행 파일·COM·위젯 등록·미리보기와 아키텍처, Authenticode Valid를 확인했다. 선택적 심볼 패키지용 `mspdbcmf.exe` 부재 경고가 남는다. ARM64 하드웨어 실행은 미실행이다. |
| x64 업데이트 설치·상태 보존 | 통과 | [설치·COM 검사](qa-evidence/design-v8/installed-package.json), [비식별 IPC](qa-evidence/design-v8/production-ipc.json). 1.0.0.8 Status Ok. 계정 1개, 적용 계정, 연결된 Windows 장치와 최신 주간 사용량이 유지됐다. 실제 인증·별칭·장치 구성을 변경하지 않았다. |
| 실제 관리 창 반응형 배치 | 세 가지 크기에서 확인 | 공식 computer-use의 실제 창 캡처는 일반 1268×894, 최대화 2560×1392, 좁은 창 844×894px였다. 최대화에서 사용량·초기화권과 장치·토큰이 각각 두 열을 채우고, 일반 창에서는 한 열, 좁은 창에서는 계정 목록이 위로 이동했다. [실행 기록](qa-evidence/design-v8/installed-ui.json). 캡처 픽셀 크기는 DIP 임계값·OS 배율 검증과 구분한다. |
| 실제 별칭 편집·주간 전용·초기화권 0개 | 표시·대화상자 확인 | 제목 옆 연필로 별칭 입력/저장/취소 대화상자를 열고 취소했다. 실제 별칭 저장은 실행하지 않았다. API가 제공하지 않는 5시간 영역은 사라지고 주간 영역이 너비를 사용한다. 초기화권은 독립 카드에 0개로 표시됐다. 실제 계정에는 양수 초기화권이 없어 만료 상세는 합성 데이터로만 확인했다. |
| 트레이 밝은·어두운 화면 | 네이티브 합성 화면 확인 | 설치본의 메모리 데모를 네이티브 렌더했다. [밝은 화면](qa-evidence/design-v8/tray-light.png), [어두운 화면](qa-evidence/design-v8/tray-dark.png). 진행률 애니메이션이 끝난 수치와 전용 초기화권 카드의 남은 시간·전체 요일·만료 시각, 고정 적용 버튼을 확인했다. Explorer 동작·화면 가장자리·OS 배율 검사를 대체하지 않는다. |
| v8 실제 위젯 보드 | 미확인 | 렌더 36개와 OS 등록·COM 검사는 통과했지만 실제 보드의 색·크기 변경·버튼·업데이트 후 연결은 아직 검증하지 않았다. v5의 표시 결과를 이월하지 않는다. |

좁은 창은 기본 시스템 메뉴의 크기 조정(Alt+Space → 크기 조정)으로 만들었으며, 실제 캡처에서 재배치를 확인했다. v7의 실패한 테두리 드래그 시도와 구분한다. QA를 마친 v8 관리 창은 최대화 상태로 열어 두었다. 실제 계정 이메일이 포함된 관리 화면 캡처는 저장소에 넣지 않고 비식별 관찰 기록만 남겼다.

## v10 창 크기 제한·왼쪽 계정·수정 버튼 정렬

이번 변경은 관리 창 배치와 네이티브 크기 제한에 한정한다. 계정·인증·위젯 동작을 변경하지 않는다. 최대 920×940 DIP / 최소 740×600 DIP를 모니터 DPI와 작업 영역에 맞춰 적용하고, 최대화를 끈다. 창 너비 880 DIP 미만에서는 왼쪽 목록을 192 DIP로 줄이고 그 이상에서는 224 DIP로 표시한다. 긴 별칭·이메일은 한 줄 말줄임으로 표시하고 툴팁으로 전체 문자열을 읽을 수 있다. 상단 편집·새로고침·설정은 기본 WinUI 버튼 스타일을 공유하는 36×36 DIP 버튼이다.

| 검사 | 결과 | 근거 및 범위 |
| --- | --- | --- |
| Windows Core 회귀 | 184 통과 / 2 제외 / 186개 | [v10 x64 빌드·테스트 로그](qa-evidence/design-v10/build-x64.log). UI 구현을 그대로 옮기는 별도 단위 테스트는 추가하지 않았다. |
| x64·ARM64 빌드·서명 MSIX | 통과 | [x64](qa-evidence/design-v10/build-x64.log), [ARM64](qa-evidence/design-v10/build-arm64.log), [패키지 검사](qa-evidence/design-v10/package-artifacts.json). ARM64 실행은 검증하지 않았다. 선택적 심볼 패키지용 `mspdbcmf.exe` 부재 경고는 유지된다. |
| x64 업데이트 설치 | 통과 | [설치 로그](qa-evidence/design-v10/install.log), [설치 등록 검사](qa-evidence/design-v10/installed-package.json). v10 Status Ok. 실제 인증·계정 설정을 수정하지 않았다. |
| 실제 창 크기·최대화 차단·좌측 목록 | 확인한 범위 통과 | 공식 computer-use에서 기본 창 캡처는 908×934, 최소 너비로 줄인 창은 728×934 논리 픽셀이었다. 양쪽 모두 계정 목록이 왼쪽에 유지됐고 버튼이 정렬됐다. 제목 표시줄 더블클릭 뒤 크기는 그대로였으며 시스템 메뉴의 최대화가 비활성화됐다. 캡처 크기는 네이티브 외곽 크기와 테두리 때문에 차이가 있다. [화면 QA 기록](qa-evidence/design-v10/installed-ui.json). |
| 작은 창 별칭 편집·재실행 | 확인한 범위 통과 | 최소 너비에서 별칭 대화상자를 열고 저장 없이 취소했다. 앱 메뉴로 종료한 뒤 다시 실행해 기본 제한 크기와 기존 계정·사용량 유지를 확인했다. [비식별 IPC](qa-evidence/design-v10/production-ipc.json). 최소 높이 조절과 최대 범위 밖으로의 드래그, 서로 다른 DPI 모니터 이동은 미검증이다. |
| 위젯·사용량·초기화권 | 이번 수정 없음 | v8의 렌더·포맷터 결과를 이전 버전 검증으로 보존한다. 실제 위젯 보드 추가 검증을 수행했다는 뜻은 아니다. |

## v11 트레이 진입·계정 영역·아이콘

| 검사 | 결과 | 근거 및 범위 |
| --- | --- | --- |
| Windows Core 회귀 | 184 통과 / 2 제외 / 186개 | [x64 빌드·테스트](qa-evidence/design-v11/build-x64.log). UI 동작을 자동으로 검증한 결과는 아니다. |
| x64·ARM64 빌드·서명 MSIX | 통과 | [ARM64 빌드](qa-evidence/design-v11/build-arm64.log), [패키지 검사](qa-evidence/design-v11/package-artifacts.json). Authenticode Valid, ZIP CRC, manifest·필수 파일·PE 아키텍처·Core 일치, 모든 패키지 아이콘과 소스 파일의 바이트 일치를 확인했다. ARM64 하드웨어 실행은 미검증이다. |
| x64 업데이트 설치·실행·상태 | 통과한 범위 명시 | [설치 로그](qa-evidence/design-v11/install.log), [등록 검사](qa-evidence/design-v11/installed-package.json), [실행 요청](qa-evidence/design-v11/launch.json), [읽기 전용 IPC](qa-evidence/design-v11/production-ipc.json). 1.0.0.11 Status Ok. 기존 계정 1개·적용 계정·연결된 Windows 장치와 최신 주간 사용량 유지. 인증·별칭·장치 설정을 변경하지 않았다. |
| 아이콘 생성·변환 | 통과 | [원본](../../Resources/WindowsIcon/SyncBarIcon.png), [내장 도구의 최종 프롬프트](../../Resources/WindowsIcon/PROMPT.md), [아이콘 검사](qa-evidence/design-v11/icon-assets.json). 16/20/24/32/40/48/64/128/256px ICO와 PNG 크기·투명도를 검사했다. 생성 후 작업은 크기 조절·포맷 인코딩에 한정한다. |
| 트레이 경로·레이아웃 | 소스·컴파일 확인 | 왼쪽 클릭과 우클릭의 앱 열기가 같은 `ShowManagementWindow`를 사용한다. 최소화 복원·표시·활성화 경로를 통합하고 일반 빠른 보기 메뉴를 제거했다. 계정 영역 336/256 DIP, 좁은 동작 영역의 계정 관리 메뉴 재배치를 컴파일했다. 실제 마우스 클릭 결과와 구분한다. |
| 실제 트레이 클릭·화면 QA | 미실행 | 공식 `node_repl/js`가 JavaScript 실행 전에 `sandboxCwd is not a local file URI`로 거부됐다. 커널 reset 후 재시도도 동일했다. [오류 기록](qa-evidence/design-v11/ui-qa.json), [기존 WSL 연결 복구 절차](WSL-COMPUTER-USE.md). 현재 Codex 작업을 중단하는 재시작은 수행하지 않았다. |

## v12 버튼 정렬·초기화 날짜·위젯 구획

사용자 제공 v11 화면에서 좁은 창의 계정 관리 버튼 행 분리, 세 번째 위젯 액션의 가로 잘림, 모호한 Windows 1 표기를 확인했다. 적용 버튼은 전체 폭, 재로그인과 계정 관리는 다음 같은 행에 고정했다. 위젯은 하단에 짧은 두 액션만 두며 큰 크기의 앱 열기는 헤더에 둔다. 시간 정보는 API가 제공한 실제 초기화 시각만 로컬 날짜와 한 글자 한국어 요일로 표시한다.

| 검사 | 결과 | 근거 및 한계 |
| --- | --- | --- |
| Windows Core | 190 통과 / 2 제외 / 192개 | [x64 로그](qa-evidence/design-v12/build-x64.log). 날짜 문화권·누락/만료, 두 크기의 날짜 표시, 두 하단 액션과 장치 수량 표기 검사 포함. |
| x64·ARM64 빌드·패키징 | 통과 | [ARM64 로그](qa-evidence/design-v12/build-arm64.log), [서명·구조·자산·해시 검사](qa-evidence/design-v12/package-artifacts.json). ARM64 하드웨어 실행은 미실행. 심볼 패키지 도구 부재 경고 1건은 이전과 동일. |
| 합성 위젯 렌더 | 72/72 | [결과](qa-evidence/widget-design-v12/results.json). 9상태 × 2크기 × 2테마 × 왼쪽/채움 정렬, 300×304/620 및 좌우·하단 버튼 경계. 실제 보드 검사가 아님. |
| 시각 검토 | 합성 PNG 확인 | [큰 주간 전용 카드](qa-evidence/widget-design-v12/large-weekly-dark-left.png), [중간 카드](qa-evidence/widget-design-v12/medium-normal-light-left.png). 중간 밝은 PNG를 새 선택기 미리보기로 포함. |
| x64 설치·시작 | 통과 | [설치](qa-evidence/design-v12/install.log), [시작](qa-evidence/design-v12/launch.json), [등록 검사](qa-evidence/design-v12/installed-package.json). 1.0.0.12 Status Ok. |
| 실제 상태 IPC | 통과 | [읽기 전용 조회](qa-evidence/design-v12/dashboard.json). 계정 1·인증 필요 0·최근 사용량 1·주간 1·5시간 미제공·장치 1 연결·오류 없음. 인증 파일 수정/실제 계정 전환 없음. |
| 실제 메인·Widgets Board 화면 | 미실행 | [컴퓨터 도구 오류](qa-evidence/design-v12/ui-qa.json). JS 실행 전 sandboxCwd WSL URI 오류로 차단. v11 첨부 화면은 원인 진단 근거이며 v12 성공 증거로 사용하지 않음. |

## 남은 실제 환경 QA

- v12 패키지 업데이트 뒤 **Codex 사용량 및 계정**의 새 디자인·제공된 한도 표시·초기화권 상세와 연결 유지 여부를 확인한다. `1.0.0.5`의 중간 카드 추가·표시는 확인했다.
- 실제 계정 2개와 검증된 WSL/SSH 대상에서 위젯의 선택만으로는 변경되지 않는지, Apply가 공통 제어 경로로 전환하는지, 실패 시 실제 파일과 표시가 복구되는지 확인한다.
- 위젯 중간/큰 크기, 고정 해제/재추가, 제공자 종료 후 재호출, 앱 종료 후 background 재실행을 확인한다.
- 고대비, 키보드·스크린리더 이름, 100/150/200% OS 배율, 배율이 다른 모니터 간 이동, 긴 별칭, 화면 가장자리에서 검증한다. 한도 정보 없음이 0%로 잘못 전달되지 않는지와 새로고침이 계정 전환 중으로 표시되지 않는지도 확인한다.
- Explorer 재시작 후 트레이 복원, 절전 복귀, Windows 로그인 시 창 없는 시작, 패키지 업데이트 후 위젯 보드의 재연결을 확인한다. 계정/사용량 보존은 위의 설치본 조회로 확인했다.

이 WSL 작업에서 Windows computer-use 도구를 차단하던 `sandboxCwd is not a local file URI` 오류를 해결했다. 같은 파일 위치의 Windows URI로 변환하는 연결 어댑터와 앱의 `CODEX_NODE_REPL_PATH` 재정의를 사용하며, 기존 승인·종료 훅을 유지한다. 최종 사용자 재실행에서 앱 환경·MCP 명령·임시 사용자 환경 복구를 모두 확인했고, 실제 `mcp__node_repl__js`의 공식 `@oai/sky.list_windows`가 성공해 CodexSyncBar를 포함한 창 6개를 반환했다. [실제 시작 검사](qa-evidence/wsl-computer-use-successful-relaunch.json), [원인·수정·복구 방법](WSL-COMPUTER-USE.md), [검증 결과](qa-evidence/wsl-computer-use-bridge.json)를 기록했다. 이어 설치본 관리 창의 실제 화면과 새로고침·WSL 관리·닫기·재열기를 검증했다. `WidgetBoard.exe`는 플러그인의 창 목록에 직접 반환되지 않았지만, 사용자가 추가 메뉴를 연 뒤 공식 도구의 Explorer 관련 캡처에서 실제 보드와 추가 목록을 확인했다. 처음 목록에는 Codex가 없었고 같은 시점의 OS 확장 카탈로그에는 등록되어 있었다. WidgetService·WidgetBoard 재시작 후 같은 `1.0.0.5`가 목록에 나타났으며, 사용자가 추가한 실제 중간 카드의 사용량 표시까지 확인했다. 직접 실행한 구형 `Dashboard/Widgets.exe`의 DLL 오류 대화상자는 닫았다. 새로고침·전환 등 버튼 실행은 표시 확인과 구분하며, 앱 화면이나 IPC 검사로 대체 통과 처리하지 않는다.

기존 큰 카드의 844×874 미리보기를 Microsoft의 [위젯 선택기 지침](https://learn.microsoft.com/en-us/windows/apps/design/widgets/widgets-picker-integration)에 맞춰 실제 중간 카드의 300×304 이미지로 새로 렌더했다. 투명한 둥근 모서리와 모든 컨트롤이 영역 안에 들어감을 검사했다. [렌더 검사](qa-evidence/widget-preview-render.json), [재현 도구](../../Scripts/Windows/WidgetPreviewExporter/README.md). 기존 `1.0.0.5` 패키지 상태에서도 보드 재시작 후 목록에 나타났으므로 이전 이미지 규격은 이번 목록 누락 원인이 아니었다.

## 테스트 중 인증 파일 사고와 복구

초기에 가져온 `WindowsPaths`는 테스트에서 명시한 임시 홈보다 부모 프로세스의 `CODEX_HOME` 환경 변수를 우선했다. 이 환경 변수가 Windows의 실제 `.codex`를 가리켜, 최초 AuthVault 테스트가 실제 `auth.json`을 합성 테스트 인증으로 덮어썼다. 사용자에게 즉시 알리고 해당 테스트를 중단했다. 별도 WSL 인증 사본은 발견하지 못했다.

명시적 테스트 홈에서는 `CODEX_HOME`/상태 경로 override를 무시하도록 수정하고 회귀 테스트를 추가했다. 빌드/테스트 스크립트도 이 환경 변수를 자식 테스트에서 제거한다. 이후 테스트는 격리된 홈에서 실행했다.

사용자가 새 전용 Chrome 창에서 직접 공식 OAuth 로그인을 완료했고, 새 인증으로 실제 API 검증을 통과했다. 사용자가 복구를 승인한 뒤 **대상 파일이 여전히 알려진 테스트 값일 때만** 새 인증으로 원자적으로 교체했다. 계정과 refresh token의 일치를 확인했으며 실행 중인 Codex 프로세스는 종료하지 않았다. 토큰이나 인증 원문은 로그/저장소/화면 증거에 기록하지 않았다.

## 재현 명령

```powershell
./Scripts/Windows/setup-cli.ps1
./Scripts/Windows/build.ps1 -Architecture x64 -Package -CertificateThumbprint YOUR_THUMBPRINT
./Scripts/Windows/build.ps1 -Architecture ARM64 -Package -CertificateThumbprint YOUR_THUMBPRINT
./Windows/scripts/test-demo-ipc.ps1 -DemoExecutable PATH_TO_APP_EXE -ReportPath demo-results.json
dotnet run --project Windows/CodexSyncBar.Windows.ManualQa -- --login
```

ManualQa는 사용자 입력이 필요한 별도 검사다. 사용자 전용 `.codex-syncbar/ManualQa/<임의 ID>` 아래 명시적 홈만 사용하고 실제 활성 인증은 변경하지 않는다. 로그인 자동화나 일반 CI 테스트에는 사용하지 않는다.

```bash
env -u CODEX_HOME -u CODEX_SYNCBAR_STATE_ROOT dotnet test Windows/CodexSyncBar.Windows.Core.Tests
bash Tests/helper-contract-tests.sh
node Tests/usage-summary-tests.mjs
node Tests/remote-codex-update-tests.mjs
bash Tests/ssh-loopback-smoke.sh /path/to/sshd
```

SSH 검사는 Linux의 `ssh`, `ssh-keygen`, `sshd`, `jq`, `python3`를 사용한다. 시스템 SSH 설정이나 실제 홈을 변경하지 않고 높은 loopback 포트의 임시 서버만 사용한다. 추출한 sshd를 사용할 때 필요한 라이브러리는 호출 환경의 `LD_LIBRARY_PATH`로 제공할 수 있다.
