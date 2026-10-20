# Android Termux SSH 장치

Ubuntu Claude SyncBar의 **장치 관리**에서 SSH 설정의 별칭을 가져와 Termux 장치를 등록할 수 있습니다. SSH 포트와 사용자는 해당 별칭의 값을 사용합니다. **설치 및 활성화** 후 다른 SSH 장치와 같은 계정 적용·로그인 동기화·Claude Code CLI 관리 경로를 사용합니다.

Termux 안에서 `openssh`, `jq`를 설치하고 SSH 연결을 준비합니다. Node.js는 필수가 아니며, 없으면 사용량 집계에 jq를 사용합니다.

```bash
pkg update
pkg install openssh jq
sshd
```

Claude Code는 Termux를 공식 지원 대상으로 문서화하지 않습니다. 이 앱은 Termux에 Claude Code가 npm 전역 패키지(`@anthropic-ai/claude-code`)로 설치되어 있다고 가정하며, 그 구성에서의 실제 동작은 이 브랜치에서 확인하지 않았습니다.

계정 등록 시 PC가 보관한 계정에서 refresh token을 뺀 로그인을 SSH로 전달합니다. refresh token은 PC에 남겨 두며 백그라운드 동기화가 갱신된 access token을 전달합니다. 태블릿의 `~/.claude/.credentials.json`은 사용자 전용 0600 파일입니다. 연결이 오래 끊겨 토큰이 만료됐다면 다시 연결해 동기화합니다.

계정 적용은 태블릿에 저장된 로그인을 바꿉니다. 열려 있는 Claude Code 세션은 자동 종료하지 않으므로 작업을 마친 뒤 종료하고 `claude`를 다시 실행합니다.

태블릿처럼 연결이 자주 끊기는 장치는 **장치 관리 → 계정 전환 실패 시 건너뛰기**를 켤 수 있습니다 (`skipOnSwitchFailure: true`, 기본값은 꺼짐). 연결·사전 확인·적용·검증에 실패한 장치는 **건너뜀**으로 표시하고 다른 장치의 전환을 계속합니다. 쓰기 이후 실패하면 해당 장치만 이전 계정으로 복구를 시도하며, 복구도 불가능하면 현재 계정을 확인할 수 없다고 표시합니다. 이 설정은 계정 로그아웃이나 CLI 업데이트 실패를 무시하지 않습니다.

## CLI 업데이트

**Claude Code CLI 관리 → tablet → 조회 / 업데이트**를 사용합니다. **모든 기기 CLI 업데이트**에서도 Termux 장치는 같은 방식으로 처리합니다.

- `claude`가 npm 전역 경로(`…/lib/node_modules/@anthropic-ai/claude-code/`)를 가리키면 `npm` 설치로 인식하고 `npm install --global @anthropic-ai/claude-code@latest`를 실행합니다.
- 설치 후 `claude --version`이 정상인지 확인합니다. 버전이 그대로면 **이미 최신 버전**으로 완료합니다.
- 설치 방식을 알 수 없는 실행 파일은 건드리지 않고 기존 설치 도구로 업데이트하라고 안내합니다.

Codex 버전에 있던 Termux 패키지(`pkg`/`apt`) 기반 업데이트 경로는 Claude Code에 해당 패키지가 없어 제거했습니다.

## 잠금 호환성 및 검증

Android에서 하드링크가 거부될 수 있어 Termux에서는 잠금 파일을 같은 비공개 디렉터리 안에서 coreutils의 no-clobber rename으로 게시합니다. 기존 파일·심볼릭 링크·디렉터리를 덮어쓰지 않으며 flock, 소유자·권한·inode 검사, 충돌 차단, 중단된 잠금 복구를 유지합니다.

`Tests/termux-lock-tests.sh`는 잠금 충돌·안전한 게시·복구를, `Tests/remote-claude-update-tests.sh`는 npm·네이티브·Homebrew 업데이트와 실패 격리를 확인합니다. `CliUpdateResultTests`는 Ubuntu 결과 표시와 일괄 업데이트 완료 판정을 확인합니다. 모두 Linux에서 Termux 환경을 흉내 낸 검사입니다.
