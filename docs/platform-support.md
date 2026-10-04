# 플랫폼 및 브랜치 지원 정책

적용일: 2026-10-04

Ubuntu를 기본이자 유일한 신규 개발 대상으로 삼습니다. 계정별 사용량 화면을 비롯한 기능, 디자인, 테스트 및 패키징은 Ubuntu 구현을 기준으로 합니다.

| 대상 | 상태 | 방침 |
| --- | --- | --- |
| Ubuntu / `main` | 활성 | 신규 기능·검증·배포의 기준 |
| Windows / `codex/windows-winui-widgets` | Deprecated | 신규 개발·정기 검증·자동 배포 중단, 이력 보존 |
| macOS / 보관된 Swift 소스 | Deprecated | 신규 개발·정기 검증·자동 배포 중단, 이력 보존 |
| 그 외 Ubuntu를 대상으로 하지 않는 브랜치 | Deprecated | 보관용, 신규 작업의 기준으로 사용하지 않음 |

Deprecated는 브랜치나 기존 릴리즈를 삭제한다는 뜻이 아닙니다. 명시적인 유지보수 요청이 있을 때만 레거시 플랫폼을 수정합니다. Ubuntu가 참조하는 공통 소스는 계속 유지합니다.

## 기본 브랜치

`main`에 Ubuntu 구현을 통합합니다. 기존 `codex/ubuntu-yaru` 작업 디렉터리는 원본 작업 보존용이며 이후 신규 개발은 `main`의 Ubuntu 구현을 기준으로 합니다.

루트 README와 자동 CI는 Ubuntu를 대상으로 합니다. macOS·Windows 워크플로는 보관 및 명시적인 수동 실행만 허용합니다.
