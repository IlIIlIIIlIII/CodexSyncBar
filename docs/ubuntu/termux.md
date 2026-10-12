# Android Termux SSH 장치

Ubuntu CodexSyncBar의 **장치 관리**에서 SSH 설정의 별칭을 가져와 Termux 장치를 등록할 수 있습니다. SSH 포트와 사용자는 해당 별칭의 값을 사용합니다. **설치 및 활성화** 후 다른 SSH 장치와 같은 계정 적용·인증 동기화·Codex CLI 관리 경로를 사용합니다.

Termux 안에서 `codex`, `openssh`, `jq`, `openssl-tool`을 설치하고 SSH 연결을 준비합니다. Codex 패키지를 제공하는 Termux 저장소가 필요합니다. Node.js는 필수가 아니며, 없으면 사용량 집계에 jq를 사용합니다.

```bash
pkg update
pkg install codex openssh jq openssl-tool
sshd
```

계정 등록 시 PC가 보관한 계정의 access-only 인증을 SSH로 전달합니다. refresh token은 PC에 남겨 두며 기존 백그라운드 동기화가 갱신된 인증을 전달합니다. 이 경로의 `auth.json`은 사용자 전용 0600 파일입니다. 태블릿 연결이 오랫동안 끊겨 인증이 만료됐다면 다시 연결해 인증을 동기화합니다.

계정 적용은 태블릿의 저장된 인증을 바꿉니다. 열려 있는 일반 Codex CLI는 자동 종료하지 않으므로 작업을 마친 뒤 종료하고 `codex`를 다시 실행합니다.

태블릿처럼 연결이 자주 끊기는 장치는 **장치 관리 → 계정 전환 실패 시 건너뛰기**를 켤 수 있습니다 (`skipOnSwitchFailure: true`, 기본값은 꺼짐). 연결·사전 확인·적용·검증에 실패한 장치는 **건너뜀**으로 표시하고 다른 장치의 전환을 계속합니다. 쓰기 이후 실패하면 해당 장치만 이전 계정으로 복구를 시도하며, 복구도 불가능하면 현재 계정을 확인할 수 없다고 표시합니다. 이후 전환은 계속 사용할 수 있습니다. 이 설정은 계정 로그아웃이나 CLI 업데이트 실패를 무시하지 않습니다.

## CLI 업데이트

**Codex CLI 관리 → tablet → 조회 / 업데이트**를 사용합니다. **모든 기기 CLI 업데이트**에서도 Termux 장치는 같은 방식으로 처리합니다.

- `$PREFIX/bin/codex`의 패키지 소유권을 확인해 `termux` 설치로 인식합니다.
- 저장소 목록을 새로 읽고 `apt-get install --only-upgrade --no-remove codex`를 실행합니다. 전체 패키지 업그레이드는 수행하지 않습니다.
- 설치 후 패키지 버전이 저장소의 candidate와 일치하는지, `codex --version`이 정상인지 확인합니다.
- Termux 빌드·미러 배포가 늦을 수 있으므로 OpenAI의 최신 버전 번호와 같아야 한다고 요구하지 않습니다.
- CLI 버전과 패키지 revision이 모두 같으면 **Termux 저장소 기준 최신 버전**으로 완료하며 실행 중인 연결을 끊지 않습니다. 패키지 revision만 바뀐 경우에는 새 패키지 적용 경로를 사용합니다.
- 저장소 조회·설치·검증 실패 시 성공으로 표시하지 않으며 기존 실행 중인 클라이언트를 종료하지 않습니다.

CodexSyncBar로 업데이트를 관리하는 태블릿은 Codex 설정의 `check_for_update_on_startup = false`로 공식 설치 채널 기준의 시작 시 업데이트 안내를 끌 수 있습니다. 이는 Termux 저장소 업데이트 자체를 끄는 설정이 아닙니다.

## 잠금 호환성 및 검증

Android에서 하드링크가 거부될 수 있어 Termux에서는 잠금 파일을 같은 비공개 디렉터리 안에서 coreutils의 no-clobber rename으로 게시합니다. 기존 파일·심볼릭 링크·디렉터리를 덮어쓰지 않으며 flock, 소유자·권한·inode 검사, 충돌 차단, 중단된 잠금 복구를 유지합니다.

`Tests/termux-lock-tests.sh`는 잠금 충돌·안전한 게시·복구를, `Tests/remote-codex-update-tests.sh`는 Termux 업데이트·최신 상태·패키지 재빌드·저장소 오류·검증 실패를 확인합니다. `TermuxCliTests`는 Ubuntu 결과 표시와 일괄 업데이트 완료 판정을 확인합니다.
