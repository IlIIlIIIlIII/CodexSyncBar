# Focus-Dismissed Transient Banners Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 한 번 표시된 성공 알림을 팝오버·설정창의 포커스 수명 주기가 끝날 때 제거하되 오류와 경고는 유지한다.

**Architecture:** `AppModel`이 현재 일회성 배너의 ID를 추적하고 동일한 배너만 해제한다. `PopoverView`와 `SettingsView`는 화면 종료 또는 비활성 전환 때 모델의 해제 API를 호출하며, 별칭 저장 성공 경로는 기존 일회성 배너 생성 API를 사용한다.

**Tech Stack:** Swift 5 language mode, SwiftUI, AppKit, XCTest, Swift Package Manager

## Global Constraints

- `계정 별칭을 저장했습니다.` 성공 알림은 처음 표시된 뒤 창을 닫거나 포커스를 잃으면 제거한다.
- 오류와 경고 알림은 포커스 해제로 제거하지 않는다.
- 기존 4초 자동 해제 동작을 유지한다.
- 계정 설정 파일 형식과 별칭 저장 로직은 변경하지 않는다.
- 배포와 설치는 수행하지 않는다.

---

### Task 1: 일회성 배너 수명 주기 모델

**Files:**
- Modify: `Tests/CodexSyncBarTests/CodexSyncBarTests.swift`
- Modify: `Sources/CodexSyncBar/AppModel.swift:79-80`
- Modify: `Sources/CodexSyncBar/AppModel.swift:1211-1224`

**Interfaces:**
- Consumes: `AppBanner.id`, `AppModel.banner`, 기존 `showTransientBanner(style:message:dismissAfterNanoseconds:)`
- Produces: `AppModel.dismissTransientBannerAfterFocusLoss() -> Void`

- [ ] **Step 1: 일회성 배너 제거 테스트 작성**

`CodexSyncBarTests`에 다음 테스트를 추가한다.

```swift
@MainActor
func testTransientBannerDismissesAfterFocusLoss() {
    let model = AppModel(readmeDemoFixture: .standard)

    model.showTransientBanner(
        style: .success,
        message: "계정 별칭을 저장했습니다.",
        dismissAfterNanoseconds: 60_000_000_000)
    XCTAssertEqual(model.banner?.message, "계정 별칭을 저장했습니다.")

    model.dismissTransientBannerAfterFocusLoss()

    XCTAssertNil(model.banner)
}

@MainActor
func testTransientBannerFocusLossPreservesReplacementError() {
    let model = AppModel(readmeDemoFixture: .standard)
    model.showTransientBanner(
        style: .success,
        message: "저장했습니다.",
        dismissAfterNanoseconds: 60_000_000_000)
    model.banner = AppBanner(style: .error, message: "확인이 필요합니다.")

    model.dismissTransientBannerAfterFocusLoss()

    XCTAssertEqual(model.banner?.message, "확인이 필요합니다.")
}
```

- [ ] **Step 2: 테스트를 실행해 RED 확인**

Run:

```bash
swift test --filter 'CodexSyncBarTests.testTransientBanner'
```

Expected: 컴파일 실패. `showTransientBanner`가 `private`이고 `dismissTransientBannerAfterFocusLoss`가 아직 정의되지 않았다는 오류가 출력된다.

- [ ] **Step 3: 모델에 최소 수명 주기 구현**

`bannerDismissTask` 옆에 현재 일회성 배너 ID를 추가한다.

```swift
private var bannerDismissTask: Task<Void, Never>?
private var transientBannerID: UUID?
```

기존 메서드를 테스트 가능한 내부 접근 수준으로 바꾸고, 일회성 ID 추적과 포커스 해제 API를 구현한다.

```swift
func showTransientBanner(
    style: BannerStyle,
    message: String,
    dismissAfterNanoseconds: UInt64 = 4_000_000_000)
{
    let nextBanner = AppBanner(style: style, message: message)
    bannerDismissTask?.cancel()
    transientBannerID = nextBanner.id
    banner = nextBanner
    bannerDismissTask = Task { [weak self] in
        try? await Task.sleep(nanoseconds: dismissAfterNanoseconds)
        guard !Task.isCancelled else { return }
        self?.dismissTransientBannerAfterFocusLoss()
    }
}

func dismissTransientBannerAfterFocusLoss() {
    bannerDismissTask?.cancel()
    bannerDismissTask = nil
    guard let transientBannerID else { return }
    self.transientBannerID = nil
    guard banner?.id == transientBannerID else { return }
    withAnimation(.easeOut(duration: 0.18)) { banner = nil }
}
```

- [ ] **Step 4: 집중 테스트를 실행해 GREEN 확인**

Run:

```bash
swift test --filter 'CodexSyncBarTests.testTransientBanner'
```

Expected: 두 테스트 모두 통과하고 실패가 0건이다.

- [ ] **Step 5: 모델 변경 커밋**

```bash
git add Sources/CodexSyncBar/AppModel.swift Tests/CodexSyncBarTests/CodexSyncBarTests.swift
git commit -m "🧪 일회성 알림 수명 주기 추가" -m "현재 일회성 배너 ID만 포커스 해제 시 제거하고 이후 오류 배너는 보존하도록 테스트와 모델 API를 추가했습니다."
```

### Task 2: 별칭 저장 및 화면 포커스 수명 주기 연결

**Files:**
- Modify: `Tests/CodexSyncBarTests/CodexSyncBarTests.swift`
- Modify: `Sources/CodexSyncBar/AppModel.swift:815-831`
- Modify: `Sources/CodexSyncBar/Views/PopoverView.swift:121-130`
- Modify: `Sources/CodexSyncBar/Views/SettingsView.swift:71-78`

**Interfaces:**
- Consumes: `AppModel.showTransientBanner(style:message:dismissAfterNanoseconds:)`, `AppModel.dismissTransientBannerAfterFocusLoss()`
- Produces: 별칭 저장 성공 알림의 일회성 표시와 팝오버·설정창 포커스 해제 연결

- [ ] **Step 1: 화면 연결 계약 테스트 작성**

`CodexSyncBarTests`에 다음 테스트를 추가한다.

```swift
func testAliasSuccessBannerUsesFocusDismissalLifecycle() throws {
    let packageRoot = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
    let appModel = try String(
        contentsOf: packageRoot.appendingPathComponent("Sources/CodexSyncBar/AppModel.swift"),
        encoding: .utf8)
    let popover = try String(
        contentsOf: packageRoot.appendingPathComponent("Sources/CodexSyncBar/Views/PopoverView.swift"),
        encoding: .utf8)
    let settings = try String(
        contentsOf: packageRoot.appendingPathComponent("Sources/CodexSyncBar/Views/SettingsView.swift"),
        encoding: .utf8)

    XCTAssertTrue(appModel.contains("""
            showTransientBanner(
                style: .success,
                message: "계정 별칭을 저장했습니다.")
    """))
    XCTAssertTrue(popover.contains("""
        .onDisappear {
            model.dismissTransientBannerAfterFocusLoss()
        }
    """))
    XCTAssertTrue(settings.contains("""
        .onDisappear {
            model.dismissTransientBannerAfterFocusLoss()
        }
    """))
    XCTAssertTrue(settings.contains("""
            } else {
                model.dismissTransientBannerAfterFocusLoss()
            }
    """))
}
```

- [ ] **Step 2: 테스트를 실행해 RED 확인**

Run:

```bash
swift test --filter 'CodexSyncBarTests.testAliasSuccessBannerUsesFocusDismissalLifecycle'
```

Expected: 테스트가 실행되지만 별칭 저장 경로와 뷰 수명 주기 문자열 assertion이 실패한다.

- [ ] **Step 3: 별칭 저장 성공 알림을 일회성으로 전환**

`updateAccountAlias`의 성공 대입을 다음 호출로 바꾼다.

```swift
showTransientBanner(
    style: .success,
    message: "계정 별칭을 저장했습니다.")
```

- [ ] **Step 4: 팝오버 화면 종료 연결**

`PopoverView.body`의 `.task` 앞에 다음 modifier를 추가한다.

```swift
.onDisappear {
    model.dismissTransientBannerAfterFocusLoss()
}
```

- [ ] **Step 5: 설정창 종료와 비활성 전환 연결**

`SettingsView.body`의 `.task` 앞에 다음 modifier를 추가한다.

```swift
.onDisappear {
    model.dismissTransientBannerAfterFocusLoss()
}
```

기존 scene phase 처리를 다음과 같이 바꾼다.

```swift
.onChange(of: scenePhase) { phase in
    if phase == .active {
        if !model.isReadmeDemo { model.refreshLaunchAtLoginState() }
    } else {
        model.dismissTransientBannerAfterFocusLoss()
    }
}
```

- [ ] **Step 6: 집중 테스트를 실행해 GREEN 확인**

Run:

```bash
swift test --filter 'CodexSyncBarTests.testAliasSuccessBannerUsesFocusDismissalLifecycle'
```

Expected: 테스트 1건이 통과하고 실패가 0건이다.

- [ ] **Step 7: 전체 테스트와 빌드 검증**

Run:

```bash
swift test
swift build
git diff --check
```

Expected: 모든 테스트 통과, 빌드 exit 0, whitespace 오류 없음.

- [ ] **Step 8: 화면 연결 변경 커밋**

```bash
git add Sources/CodexSyncBar/AppModel.swift Sources/CodexSyncBar/Views/PopoverView.swift Sources/CodexSyncBar/Views/SettingsView.swift Tests/CodexSyncBarTests/CodexSyncBarTests.swift
git commit -m "✨ 확인한 성공 알림 자동 제거" -m "별칭 저장 성공 알림을 일회성으로 전환하고 팝오버와 설정창의 종료·포커스 해제 시 해당 알림만 제거하도록 연결했습니다."
```
