# WSL에서 Windows Computer Use 연결 수정

2026-09-19, Codex Desktop `26.915.4065.0`, WSL app-server `0.155.0-alpha.9.2`,
`cua_node 0.0.16/20260915001755-492f19756c31`에서 확인했다.

## 원인

WSL app-server가 Windows `node_repl.exe`에 다음 메타데이터를 보냈다.

```text
codex/sandbox-state-meta.sandboxCwd = file:///home/sunggu/Projects/Personal/CodexSyncBar
```

Windows 실행기는 이 URI를 Windows 로컬 경로로 변환할 수 없어 JavaScript 실행 전에
`-32602: sandboxCwd is not a local file URI`로 거부했다. 공식 실행 파일에 포함된
오류 위치는 `src/sandbox_state.rs`다. `js_reset`으로는 경로를 보내는 연결이 바뀌지 않는다.

같은 파일 시스템 위치의 올바른 Windows URI는 다음과 같다.

```text
file://wsl.localhost/Ubuntu-26.04/home/sunggu/Projects/Personal/CodexSyncBar
```

## 수정 범위

[`node-repl-wsl-bridge.py`](../../Scripts/Windows/node-repl-wsl-bridge.py)는 WSL과 공식
Windows `node_repl.exe` 사이에서 MCP 표준 입출력을 전달한다. `tools/call` 요청의
`codex/sandbox-state-meta` 안에 있는 파일 URI만 `wslpath -w`로 같은 Windows 경로로
변환한다. 권한 결정, 읽기/쓰기/거부 규칙, 네트워크 설정, 승인, 도구 인자, 서버 응답은
변경하지 않는다. 변환 실패 시 요청을 거부하며 메타데이터를 제거하거나 완화하지 않는다.
UI 조작은 기존 공식 `node_repl`과 `@oai/sky`가 수행한다.

Codex 앱은 시작·작업 재개 시 `mcp_servers.node_repl` 설정을 다시 생성한다.
따라서 최종 수정은 앱 코드에 구현된 `CODEX_NODE_REPL_PATH` 실행 경로 재정의를 사용한다.
기존 `node_repl` 이름과 승인 정책·종료 훅을 유지하고, 앱이 넘겨주는 최신 Node 실행 경로에서
공식 `node_repl.exe` 위치를 찾아 실행한다. 네이티브 연결 주소도 앱이 공급한 현재 값을 그대로 사용한다.
별도 서버 이름으로 복제하거나 공식 실행 파일을 변경하지 않았다.

현재 PC에는 아래 위치로 설치했다.

- 어댑터: `/home/sunggu/.local/lib/codex-wsl-node-repl/bridge.py`
- 실행 도우미: `C:\Users\youns\AppData\Local\CodexSyncBarBuild\wsl-computer-use\Start-Codex-WSL.cmd`
- 설정: 수동 시험의 `command`, `args` 변경을 복구한 뒤, 앱이 실행 경로 재정의에 따라
  `mcp_servers.node_repl.command = "/home/sunggu/.local/lib/codex-wsl-node-repl/bridge.py"`를 생성했다.
- 원본 백업: `C:\Users\youns\.codex\config.toml.before-wsl-node-repl-20260919T060921Z`
- 복구용 원래 명령/인자 기록: `/home/sunggu/.local/lib/codex-wsl-node-repl/installation.json`

Codex를 완전히 종료한 뒤 실행 도우미를 실행하고, 같은 작업을 다시 열어 이어간다.
도우미는 시작하는 동안만 사용자 환경의 `CODEX_NODE_REPL_PATH`를 임시 등록하고,
최종 프로세스와 MCP 명령 확인 후 원래 값으로 복구한다. 복구 기록과 실행 잠금을 사용하며
중간에 사용자가 다른 값으로 바꾸면 그 변경을 보존한다. 시스템 환경 변수나 권한 설정을
변경하지 않고, 실행 중인 Codex를 강제 종료하지 않는다. WSL 에이전트 환경을 유지해야 한다.
기본 실행 방식으로 되돌리려면 앱을 종료한 뒤 일반 Codex 바로가기로 실행한다.

원본 실행 도우미는 [`start-codex-wsl.ps1`](../../Scripts/Windows/start-codex-wsl.ps1)이다.
`-CheckOnly` 검사에서 WSL 어댑터와 Windows 앱 경로가 모두 존재하며, 기존 앱이 실행 중인
것을 확인했다. 실제 앱 재시작 자체는 현재 작업 연결을 끊으므로 사용자에게 맡긴다.

## 첫 재실행에서 발견한 실행 도우미 문제

사용자가 실행 도우미를 실행한 뒤에도 실제 도구 호출은 같은 오류를 반환했다.
새 Codex 메인 프로세스 PID 27892에 `CODEX_NODE_REPL_PATH`가 없고, 앱 자체의 런타임 선택
로그도 `nodeReplPathSource=bundled-or-dev`인 것을 확인했다. WSL app-server도 새 프로세스였다.
따라서 이전 MCP 프로세스만 남아 있던 상황과 구분한다. 초기 실행 도우미의 전달 실패이며,
Windows native/MSIX 실행 단계 중 어느 단계에서 변수가 빠졌는지는 확정하지 않았다.

실행 도우미를 `ProcessStartInfo.UseShellExecute=false`와 명시적인 환경 변수 블록으로 변경했다.
시작 후 안정된 Codex 프로세스의 해당 환경 변수 한 개와 생성된 `node_repl` 명령을 자동 검사해
같은 폴더의 `startup-verification.json`에 결과를 기록한다. 환경 변수를 전부 출력하거나
실행 중인 앱을 종료하지 않는다. [진단 스크립트](../../Scripts/Windows/inspect-codex-override.ps1)는
읽기 권한만 사용하며 지정된 키 이외의 환경 값은 반환하거나 기록하지 않는다.

수정된 실행 방식은 격리된 Windows 자식 프로세스의 환경 전달·실제 값 조회·PowerShell 구문·
WSL 경로 대조 검사를 통과했다. 선택적인 `WSL_DISTRO_NAME` 변수가 없어도 공식 실행기 연결을
통과하도록 호스트 커널로 WSL을 확인한다.

## 두 번째 재실행과 확인된 원인

명시적인 프로세스 환경 블록을 적용한 뒤에도 사용자 재실행에서 부모 PID 33860과 최종
PID 49104가 달랐고, 최종 환경·MCP 명령 검사는 모두 실패했다.
[실제 시작 검사 결과](qa-evidence/wsl-computer-use-second-relaunch.json)를 남겼다.

최종 프로세스 명령에는 Chromium이 자동으로 권한을 낮춰 재실행할 때 추가하는
`--do-not-de-elevate`가 있었다. Chromium의 `MaybeAutoDeElevate` → `RunDeElevated` 경로는
`CreateProcessWithTokenW`에 `lpEnvironment=null`을 전달한다. 이 API는 호출자 환경 대신
대상 사용자 프로필로 환경을 새로 만든다. [Microsoft API 문서](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw),
[Chromium 실행 코드](https://chromium.googlesource.com/chromium/src/+/main/base/win/elevation_util.cc)를 확인했다.
따라서 프로세스 환경 전달 방식만 바꿔서는 이 경로를 해결할 수 없었다.

최종 실행 도우미는 위 사용자 환경의 임시 등록·복구 방식을 사용한다. 권한을 낮추는
동작을 끄거나 Codex를 관리자 권한으로 유지하는 플래그는 추가하지 않는다.
부모 프로세스에 수정 값이 없는 상태에서도 Windows `CreateEnvironmentBlock`이 임시 사용자
설정을 포함한 환경을 만드는 검사는 통과했고, 원래 사용자 설정 복원을 확인했다.
정확한 `CreateProcessWithTokenW` 자식 실행 검사는 일반 권한에서 오류 1314로 차단되어
[미실행으로 기록](qa-evidence/wsl-null-environment-launch.json)했다. 권한을 활성화하지 않았다.

## 최종 재실행과 실제 도구 연결 성공

사용자가 최종 실행 도우미로 재실행한 뒤, 2026-09-19 06:47:36 UTC에 시작한 실행에서
최종 Codex 프로세스 PID 42056의 환경 변수와 앱이 생성한 MCP 명령을 모두 확인했다.
`EnvironmentVerified`, `ConfigurationVerified`, `UserEnvironmentRestored`가 모두 `true`다.
[실제 시작 검사 결과](qa-evidence/wsl-computer-use-successful-relaunch.json)를 보관했다.

같은 WSL 작업에서 실제 `mcp__node_repl__js`로 공식 `@oai/sky`를 가져오고
`list_windows`를 호출하는 데 성공했다. 반환된 창 6개에 실행 중인 CodexSyncBar 창이
포함됐다. 원래 발생하던 JavaScript 실행 전 경로 오류가 해결됐으며, 별도 작업으로
옮기거나 승인 정책·권한을 완화하지 않았다. 이 확인은 도구 연결과 창 목록 조회의
검증이며, 위젯 보드 추가·사용량 표시·계정 전환의 화면 QA 결과는 아니다.

## 검증

- `python3 Tests/node-repl-wsl-bridge-tests.py`: 12개 통과.
- 같은 공식 Windows 실행 파일과 같은 권한 프로필로 별도 MCP 연결을 만들었다.
  수정 전에는 원래 오류가 재현됐고, 수정 후에는 `nodeRepl.write`의 고정 문자열 출력이
  성공했다. 앱이 사용할 인자 없는 실행 진입점으로도 같은 결과를 확인했다.
  이 검사는 인증 파일이나 Windows UI를 조작하지 않았다.
- 첫 번째·두 번째 재실행에는 원래 오류가 남았고, 사용자 환경 임시 등록·복구를 적용한
  최종 재실행에서 실제 `mcp__node_repl__js`와 `@oai/sky.list_windows`가 성공했다.
  `js_reset`만으로는 앱 프로세스에 누락된 실행 경로 재정의를 적용할 수 없다.
- 실제 창 목록 조회에서 CodexSyncBar를 확인했다. 위젯 보드 표시와 상호작용은 별도
  화면 QA가 필요하며, 이 연결 회귀 검사만으로 통과 처리하지 않는다.

공식 소스 참고: [MCP sandbox 메타데이터 구성](https://github.com/openai/codex/blob/main/codex-rs/core/src/mcp_tool_call.rs),
[SandboxState 형식](https://github.com/openai/codex/blob/main/codex-rs/codex-mcp/src/runtime.rs).
