# Windows 위젯 제공자

Windows App SDK 2.5.1의 `IWidgetProvider`를 구현하는 별도 COM 실행 파일이다. WinUI 앱과 함께 MSIX에 포함해야 Windows 위젯 보드에서 검색·고정할 수 있다. `net10.0-windows10.0.22621.0`을 대상으로 x64와 ARM64를 빌드한다.

## 배포 계약

- 실행 파일: 패키지 루트의 `Widgets\CodexSyncBar.Windows.Widgets.exe`. 해당 프로젝트의 publish 결과 전체를 `Widgets`에 포함한다.
- COM 클래스 ID: `D946A8F4-089F-4FD3-AF58-372F5033415D`.
- 위젯 정의 ID: `CodexSyncBar.Usage`. 지원 크기는 `medium`, `large`.
- 같은 패키지 루트의 `CodexSyncBar.Windows.exe --background`로 앱을 시작한다. 앱은 사용자별 `DashboardPipeServer.DefaultPipeName`에 서버를 열어야 한다.
- 패키지 매니페스트의 `windows.comServer` 등록과 `com.microsoft.windows.widgets` app extension의 `CreateInstance`는 같은 COM ID를 사용한다. 위젯 아이콘과 미리보기 이미지를 패키지에 실제로 포함한다.

프로젝트의 Windows App SDK 자체 포함 설정과 배포 매니저 자동 초기화 비활성화는 별도 프로세스를 같은 MSIX에서 실행하기 위한 설정이다. 제공자를 단독으로 실행하는 것만으로 위젯이 등록되지는 않는다.

## 동작과 IPC

`GetWidgetInfos`로 재시작 전 고정된 위젯을 복원한다. 각 위젯의 `CustomState`에는 진행 중 작업 ID만 저장한다. 계정 토큰·SSH 설정·인증 파일은 제공자가 읽지 않는다. WinRT callback 인자는 callback 내부에서 값만 복사한다.

현재 계정의 별칭과 가린 이메일을 표시하고, 5시간·주간 **남은 비율**을 큰 숫자로 보여준다. API가 5시간 한도를 제공하지 않으면 해당 열 전체를 숨기고 주간 한도가 가로 공간을 채운다. 큰 크기는 초기화 시각·초기화권 상세·장치 적용 집계를 추가한다. Spark 한도는 제공하지 않는다. 선택 자체는 계정을 변경하지 않는다. 적용은 계정 ID, 구성 revision, 요청 ID를 앱에 전달한다. 앱의 공유 controller가 동시 실행, 사전 점검, 검증과 복구를 담당한다. 갱신 날짜·시각과 상태를 표시하며, 오래된 정상 값은 마지막 확인 값으로 구분한다. 중간 크기의 오류는 짧은 상태 요약으로 표시해 선택·적용·새로고침 위치를 유지하고, 큰 크기와 앱에서 자세한 상태를 확인한다. 전환 중에는 중복 변경을 막고 앱에서 상태를 확인하는 버튼을 제공한다.

큰 위젯의 초기화권은 별도 배경 영역에 총 수량과 만료 시각별 수량, 남은 일·시간·분, `yyyy.MM.dd (월요일) HH:mm` 형태의 로컬 만료 시각을 표시한다. 가장 가까운 두 만료 그룹을 보여 주며 나머지는 앱에서 전체 확인하도록 안내한다. 0개와 수량 미확인을 구분하고 API에 없는 만료 시각은 만들지 않는다. 중간 크기는 조작 버튼이 잘리지 않도록 사용량·계정 조작에 집중한다.

파이프는 `PipeOptions.CurrentUserOnly`로 제한한다. 메시지는 4바이트 little-endian 길이와 UTF-8 JSON이다. 요청 최대 4 KiB, 응답 최대 256 KiB, 읽기 최대 10초, 동시 연결 최대 16개다. `GetSnapshot`, `RefreshUsage`, `SwitchAccount`, `GetOperationStatus`, `OpenSettings`만 허용하며 알 수 없는 JSON 속성을 거절한다. 연결이 끊겨도 이미 접수한 변경은 앱에서 계속 처리한다. 동일 전환 요청 ID는 앱 프로세스 내에서 한 번만 실행된다.

## 격리된 실제 위젯 QA

개발용 staging 매니페스트의 `com:ExeServer`에만 `Arguments="--demo"`를 추가하면 제공자는 `.demo` 파이프와 앱의 `--demo --background` 모드를 사용한다. 제품 매니페스트에는 이 인자를 넣지 않는다. 이 경로는 계정 파일에 접근하지 않는 WinUI 앱의 합성 데이터를 사용하므로 실제 위젯 보드에서 선택·적용·새로고침과 복구 상태를 안전하게 확인할 수 있다.

1. Microsoft 문서의 개발 사전 조건대로 Windows 개발자 모드를 활성화하고 서명된 개발 MSIX를 설치한 뒤 `Win+W`에서 **Codex 사용량 및 계정**을 추가한다.
2. 중간·큰 크기의 한도·갱신 시각·현재 계정을 확인한다. 계정 선택만으로 현재 계정이 바뀌지 않아야 한다.
3. 적용 버튼을 연속으로 누르고 한 작업만 수행되는지 확인한다. 완료 후 현재 계정과 장치 요약을 확인한다.
4. 앱을 종료하고 새로고침해 백그라운드 재시작을 확인한다. 제공자 프로세스를 종료한 뒤 위젯 보드를 다시 열어 고정 상태와 작업 상태가 복원되는지 확인한다.
5. 위젯을 고정 해제하면 마지막 위젯이 제거된 제공자가 종료되는지 확인한다. 배율·테마·고대비·키보드·긴 별칭도 점검한다.

합성 계정으로 통과한 QA는 실제 OAuth·WSL·SSH 전환 성공으로 기록하지 않는다. 실제 장치 전환은 별도로 검증한다. 로그는 `%LOCALAPPDATA%\CodexSyncBar\Logs\widgets.log`에 작업 이름·복원 개수·예외 유형·HRESULT와 메서드 이름만 기록한다. 계정 이름·요청 값·예외 메시지·소스 경로는 기록하지 않는다.

설치 등록 확인은 `Scripts/Windows/verify-package.ps1 -ActivateWidgetProvider`로 수행한다. 이 명령은 설치된 매니페스트·파일·개발자 모드 값을 확인하고 COM class factory만 활성화한다. `-ConstructWidgetProvider`를 더하면 제공자 생성 및 `WidgetManager.GetWidgetInfos` 초기화도 호출한다. 공유 앱의 조회 경로가 시작될 수 있으나 계정 전환이나 위젯 보드의 화면 검증은 수행하지 않는다. 개발 빌드 업데이트는 `install.ps1 -PackagePath <MSIX> -CloseRunningApp -ForceUpdateFromAnyVersion`으로 실행할 수 있으며, 같은 identity·version의 다른 패키지를 Windows가 거절하면 매니페스트 버전을 올려 다시 빌드한다.

## 자동 검증

`CodexSyncBar.Windows.Core.Tests`의 `WidgetContractTests`는 실제 named pipe 연결, 프레임 제한과 잘못된 요청, stale revision, 중복·충돌 요청, client 연결 종료, 비밀을 제거한 오류, 전환 중 UI, 사용량 누락·비정상 수치·잔여 비율·오래된 캐시 표시와 두 한도를 검증한다.

근거 문서: [Microsoft의 C# 위젯 제공자 구현](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-cs), [Windows App SDK 릴리스](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels).

## 디자인과 크기 검증

Adaptive Cards 1.5의 `TextBlock`, `ColumnSet`, `Column`, `Container`, `Input.ChoiceSet`, `Action.Execute`만 사용한다. 지원되지 않는 `ProgressBar`나 직접 만든 이미지 막대는 넣지 않는다. `Good`·`Accent`로 한도를 구분하고 남은 비율 20% 이하는 `Warning`, 초기화권 영역은 `emphasis`를 사용한다. 실제 색은 호스트 테마가 결정하며 문구·수치도 함께 표시해 색만으로 의미를 전달하지 않는다. 현재 계정과 전환할 계정을 구분하고, 14px 본문·28px 핵심 수치·12px 보조 정보의 계층으로 남은 한도를 먼저 읽게 한다.

중간 카드의 선택·적용·새로고침은 3개 조작 대상이다. 큰 카드에서는 앱 열기를 추가한다. 큰 카드의 장치 상태는 긴 장치 이름을 나열하는 대신 `3/3 적용됨`, Windows/WSL/SSH 수량, 오프라인·다른 계정 수로 요약한다. 계정이 없으면 로그인 안내, 전환 중이면 진행 안내로 바뀐다.

[WidgetPreviewExporter](../../Scripts/Windows/WidgetPreviewExporter/README.md)는 실제 Core 템플릿과 메모리 전용 합성 데이터를 사용한다. 300×304 중간·300×620 큰 크기에서 호스트 표시 영역 48px, 안쪽 여백 16px와 공식 글꼴 크기를 모사해 일반·긴 별칭/오류·계정 없음·전환 중·오프라인·주간 한도만 있는 상태를 양 테마로 검사한다. 파서 경고나 높이/너비 초과가 있으면 실패하며 실제 Windows 위젯 보드·DPI·스크린리더 QA와 구분한다.

설계 근거: [위젯 디자인 기초](https://learn.microsoft.com/en-us/windows/apps/design/widgets/widgets-design-fundamentals), [위젯 상호작용 지침](https://learn.microsoft.com/en-us/windows/apps/design/widgets/widgets-interaction-design), [선택기 미리보기 규격](https://learn.microsoft.com/en-us/windows/apps/design/widgets/widgets-picker-integration). 계정 적용은 사용자가 명시적으로 요청한 위젯 내 동작이며, 상세 관리는 앱에서 수행한다.
