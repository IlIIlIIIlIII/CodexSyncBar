# README UI Demo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 실제 SwiftUI 화면을 가상 데이터로 렌더링한 메인·설정 PNG를 README에 안전하게 게시한다.

**Architecture:** `ReadmeDemoCommand`가 캡처 전용 명령행 인자를 엄격하게 해석하고, `ReadmeDemoFixture`가 고정된 문서용 데이터를 제공한다. `AppModel`은 명시적인 데모 적용 메서드에서만 해당 상태를 받아들이며, `ReadmeCaptureController`가 앱 소유 `NSWindow`의 content view를 PNG로 저장하고 종료한다. 일반 메뉴 막대 실행 경로는 변경하지 않는다.

**Tech Stack:** Swift 6, SwiftUI, AppKit, XCTest, Bash, Markdown

## Global Constraints

- 캡처에는 실제 계정, 호스트, 사용량, 시각 또는 로컬 경로를 넣지 않는다.
- 가상 계정은 `demo.main@example.com`, `demo.sub@example.com`을 사용한다.
- 호스트는 `workstation.example.net`, `build.example.net`만 사용한다.
- 네트워크, Keychain, SSH, 인증 파일 및 사용자 설정을 읽거나 변경하지 않는다.
- 산출물은 `docs/images/readme-popover.png`, `docs/images/readme-settings.png` 두 장이다.

---

### Task 1: 캡처 명령과 안전한 fixture 계약

**Files:**
- Create: `Sources/CodexSyncBar/ReadmeDemo.swift`
- Modify: `Tests/CodexSyncBarTests/CodexSyncBarTests.swift`

**Interfaces:**
- Produces: `ReadmeDemoCommand.parse(arguments:) -> ReadmeDemoCommand?`
- Produces: `ReadmeDemoFixture.standard` with profiles, usage, devices and token usage

- [x] **Step 1: Write the failing tests**

명령이 `--readme-demo=popover|settings`와 절대 출력 경로만 허용하고 fixture 문자열이 `example.com`/`example.net` 이외의 이메일·IP·사용자 홈 경로를 포함하지 않는 테스트를 추가한다.

- [x] **Step 2: Run tests to verify RED**

Run: `swift test --filter 'testReadmeDemo'`

Expected: `ReadmeDemoCommand`와 `ReadmeDemoFixture`가 없어 컴파일 실패한다.

- [x] **Step 3: Implement the minimal parser and fixture**

```swift
enum ReadmeDemoScreen: String { case popover, settings }

struct ReadmeDemoCommand: Equatable {
    let screen: ReadmeDemoScreen
    let outputURL: URL

    static func parse(arguments: [String]) -> Self? {
        let screenValues = arguments.compactMap { argument -> String? in
            guard argument.hasPrefix("--readme-demo=") else { return nil }
            return String(argument.dropFirst("--readme-demo=".count))
        }
        let outputValues = arguments.compactMap { argument -> String? in
            guard argument.hasPrefix("--readme-output=") else { return nil }
            return String(argument.dropFirst("--readme-output=".count))
        }
        guard screenValues.count == 1,
              outputValues.count == 1,
              let screen = ReadmeDemoScreen(rawValue: screenValues[0]),
              outputValues[0].hasPrefix("/")
        else { return nil }
        return Self(
            screen: screen,
            outputURL: URL(fileURLWithPath: outputValues[0]).standardizedFileURL)
    }
}
```

`ReadmeDemoFixture.standard`는 고정된 표시 시각과 캡처 시작 기준의 상대 만료 시각, 위 Global Constraints의 계정·호스트, 별칭 `메인`/`서브`, 장치 표시명 `작업 서버`/`빌드 서버`, 주간 잔여량 `68%`/`42%`, 고정 토큰 합계 `12,345,678`을 사용한다.

- [x] **Step 4: Run tests to verify GREEN**

Run: `swift test --filter 'testReadmeDemo'`

Expected: 새 데모 계약 테스트가 모두 통과한다.

### Task 2: 실제 뷰의 격리된 PNG 캡처

**Files:**
- Create: `Sources/CodexSyncBar/ReadmeCaptureController.swift`
- Modify: `Sources/CodexSyncBar/AppModel.swift`
- Modify: `Sources/CodexSyncBar/AppDelegate.swift`
- Modify: `Sources/CodexSyncBar/CodexSyncBarApp.swift`
- Modify: `Tests/CodexSyncBarTests/CodexSyncBarTests.swift`

**Interfaces:**
- Consumes: `ReadmeDemoCommand`, `ReadmeDemoFixture.standard`
- Produces: `AppModel.init(readmeDemoFixture:)`
- Produces: `ReadmeCaptureController.capture(command:model:)`

- [x] **Step 1: Write the failing state-isolation test**

데모 fixture 적용 후 계정, 선택 계정, 인증 상태, 장치 상태와 사용량이 고정값이 되고 `start()`가 외부 작업을 시작하지 않는 테스트를 추가한다.

- [x] **Step 2: Run the test to verify RED**

Run: `swift test --filter 'testReadmeDemoModel'`

Expected: `AppModel`의 데모 fixture 초기화 경로가 없어 컴파일 실패한다.

- [x] **Step 3: Implement the minimal demo model path**

`AppModel` 내부에서만 private-set 상태를 fixture로 교체하고 `hasStarted = true`로 설정한다. `AppDelegate`는 캡처 명령일 때 일반 preview와 로그인 분기보다 먼저 데모 모델과 캡처 컨트롤러를 사용한다. `CodexSyncBarApp.isSpecialLaunch`에도 `--readme-demo=`를 포함한다.

- [x] **Step 4: Implement app-owned view capture**

`NSHostingView`를 고정 크기 창에 넣고 레이아웃 완료 후 `bitmapImageRepForCachingDisplay`와 `cacheDisplay`로 PNG를 만든다. 출력 부모가 존재하고 일반 디렉터리이며 출력이 심볼릭 링크가 아닐 때만 원자적으로 기록하고 앱을 종료한다.

- [x] **Step 5: Run focused and full tests**

Run: `swift test --filter 'testReadmeDemo' && swift test`

Expected: 데모 테스트와 기존 전체 테스트가 실패 없이 통과한다.

### Task 3: 재현 가능한 캡처와 README 배치

**Files:**
- Create: `Scripts/capture-readme-ui.sh`
- Create: `docs/images/readme-popover.png`
- Create: `docs/images/readme-settings.png`
- Modify: `README.md`

**Interfaces:**
- Consumes: built `CodexSyncBar` executable and `--readme-demo` command
- Produces: two deterministic PNG files referenced by README

- [x] **Step 1: Add the capture script**

스크립트는 임시 `HOME`과 임시 `UserDefaults` suite를 사용해 debug 앱을 빌드하고 popover/settings 캡처 명령을 각각 실행한다. 대상은 저장소의 `docs/images`로 고정한다.

- [x] **Step 2: Generate and inspect both images**

Run: `bash Scripts/capture-readme-ui.sh`

Expected: 두 PNG가 0바이트보다 크고 `sips -g pixelWidth -g pixelHeight`에서 고정된 양수 크기를 가진다.

- [x] **Step 3: Add README UI section**

앱 소개 다음에 `## 사용자 UI`를 추가하고 `docs/images/readme-popover.png`, `docs/images/readme-settings.png`를 각각 메인 메뉴와 설정 설명 아래에 배치한다. 모든 값이 예시임을 한 문장으로 밝힌다.

- [x] **Step 4: Privacy and visual verification**

Run: 허용된 `demo.*@example.com` 외 이메일, IP 주소와 사용자 홈 절대 경로를 각각 검색한다.

Expected: 결과 없음. 두 PNG를 직접 열어 텍스트 잘림, 스크롤, 빈 공간과 비정상 배율이 없는지 확인한다.

- [x] **Step 5: Final verification and publish**

Run: `bash Tests/helper-contract-tests.sh && swift test && git diff --check`

Expected: helper 계약 테스트와 Swift 전체 테스트가 통과하고 diff 오류가 없다. 의도한 파일만 커밋해 `main`에 푸시한 뒤 GitHub README의 이미지 URL이 HTTP 200인지 확인한다.
