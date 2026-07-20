# Settings, Multi-Account, and SSH Device Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use test-driven-development for each behavior slice, requesting-code-review before release, and verification-before-completion before claiming success.

**Goal:** Ship Codex SyncBar 2.0.0 with a dedicated settings window, dynamic accounts, dynamic SSH devices and Keychain-backed secrets while preserving all existing credentials and browser sessions.

**Architecture:** Stable positive account IDs continue to derive auth and Chromium paths. A mode-0600 versioned config stores account order/full email and SSH metadata. Keychain stores only SSH passwords/passphrases. The existing `gpt-switch` transaction engine is generalized to dynamic accounts/devices and remains the only distributed mutation boundary.

**Tech stack:** Swift 6 package in Swift 5 language mode, SwiftUI/AppKit, Security framework, ServiceManagement, XCTest, Bash 3-compatible helper, macOS 13+.

**Global constraints:** Never rename existing auth/browser profiles; never expose secrets; never make the fixed 368×720 popover scroll; do not use Main/Sub labels; do not invoke legacy swap from UI; use only `.venv` if Python becomes necessary; this directory is non-Git, so use test logs and artifact hashes as checkpoints instead of commits.

---

## Task 1: Persisted configuration, migration, and validation

**Files:**

- Create: `Sources/CodexSyncBar/AppConfiguration.swift`
- Create: `Sources/CodexSyncBar/KeychainStore.swift`
- Modify: `Sources/CodexSyncBar/Models.swift`
- Test: `Tests/CodexSyncBarTests/CodexSyncBarTests.swift`

1. Add failing tests for lossless two-account/three-device bootstrap, three-account ordering, monotonic IDs, invalid/symlink config rejection, SSH field validation, and JSON secret absence.
2. Run `swift test --filter Configuration` and preserve the expected compile/test failure as the RED checkpoint.
3. Implement `ManagedAccount`, `SSHDeviceConfiguration`, `AppConfiguration`, atomic mode-0600 load/save, default migration, reorder, add/remove, and a Keychain protocol/system implementation.
4. Run the focused tests until GREEN, then run `swift test`.

## Task 2: Generalize the helper using shell-level contract tests

**Files:**

- Modify: `Support/gpt-switch`
- Create: `Support/codex-syncbar-askpass`
- Create: `Tests/helper-contract-tests.sh`
- Modify: `build-app.sh`
- Modify: `Resources/Info.plist`

1. Add failing shell contract cases for positive ID 3, dynamic account discovery, dynamic device config, private-key/certificate/password argv, structured status, all-account duplicate scanning, and secret redaction.
2. Run `bash Tests/helper-contract-tests.sh` and retain the expected RED output.
3. Add versioned config loading with safe defaults; replace `1|2` validation and fixed loops; add `accounts-json`, `status-json`, `test-device`, explicit logout fallback, and dynamic remote arrays.
4. Build SSH argv from validated fields, retain strict host-key checks, and use Keychain askpass for password/passphrase modes. Keep legacy OpenSSH config only for migrated devices.
5. Run `bash -n Support/gpt-switch Support/codex-syncbar-askpass`, the contract suite, then `swift test`.

## Task 3: Dynamic Swift services and model

**Files:**

- Modify: `Sources/CodexSyncBar/AuthStore.swift`
- Modify: `Sources/CodexSyncBar/SwitchService.swift`
- Modify: `Sources/CodexSyncBar/AppModel.swift`
- Modify: `Sources/CodexSyncBar/AppDelegate.swift`
- Modify: `Sources/CodexSyncBar/LoginCoordinator.swift`
- Modify: `Sources/CodexSyncBar/ChromiumBrowserController.swift`
- Test: `Tests/CodexSyncBarTests/CodexSyncBarTests.swift`

1. Add failing tests for N-account status parsing, duplicate checks across every account, dynamic refresh, account reservation cancellation, selection fallback, and explicit-fallback logout.
2. Run focused tests for a RED checkpoint.
3. Load profiles/devices from configuration, replace fixed dictionaries and two-task refresh with dynamic collections/task groups, accept every positive login-profile argument, and expose settings-only account/device mutations.
4. Preserve legacy swap recovery but remove normal swap entry points. Make every login target explicit and persist the authenticated full email.
5. Run focused tests and full `swift test` to GREEN.

## Task 4: Settings UI and fixed popover

**Files:**

- Create: `Sources/CodexSyncBar/Views/SettingsView.swift`
- Modify: `Sources/CodexSyncBar/CodexSyncBarApp.swift`
- Modify: `Sources/CodexSyncBar/Views/PopoverView.swift`
- Modify: `Sources/CodexSyncBar/Design.swift`
- Test: `Tests/CodexSyncBarTests/CodexSyncBarTests.swift`

1. Add source/UI contract tests that initially fail: settings scene exists; Accounts/Devices/General actions are present; popover contains no ScrollView, login, refresh, swap, or logout; full email is shown; device count is dynamic.
2. Add a `Window(id: "settings")` scene with `NavigationSplitView`, account drag reorder/add/login/refresh/logout/remove, SSH device editor and test, and launch-at-login/refresh controls.
3. Replace the fixed two-tab selector with a compact full-email menu; cap the popover device preview and show an overflow row; retain exact popover dimensions.
4. Run `swift test` and launch `--preview-window` to confirm compilation and layout.

## Task 5: Release migration, deployment, and live verification

**Files:**

- Modify: `README.md`
- Modify: `Resources/Info.plist`
- Generate: `dist/Codex SyncBar.app`
- Generate: `../../outputs/Codex SyncBar.app`
- Generate: `../../outputs/Codex SyncBar.zip`

1. Record SHA-256 and mode of existing `1.auth.json`, `2.auth.json`, `current`, and Chromium profile paths before first 2.0 launch.
2. Run `swift test`, helper contract tests, `swift build -c release`, `./build-app.sh`, `codesign --verify --deep --strict`, `plutil -lint`, and bundle version/helper parity checks.
3. Review the complete diff-equivalent file list and test output using the requesting-code-review checklist; fix every validated issue and rerun affected tests.
4. Install the bundled helper and askpass with safe modes, deploy the matching helper to the three configured SSH hosts using the existing verified access path, then atomically replace `/Applications/Codex SyncBar.app`.
5. Launch the app, migrate configuration, and recheck auth hashes/modes/current marker/Chromium directories for preservation.
6. Exercise account selection, refresh, settings opening, reorder persistence, and a non-destructive SSH connection test on every device. Use a rendered UI inspection to prove the popover has no scroll bar and management actions exist only in settings.
7. Save final screenshots and verification logs under `../../outputs`, then report installed version, selected account, device parity, tests, and any unreachable-device caveat.

## Plan Self-Review

Every requested behavior has an automated or rendered/live proof boundary. The order deliberately makes persistence and helper contracts stable before the UI depends on them. Existing auth and Chromium state is hashed before migration and compared afterward. Secrets have an explicit negative test. The only intentionally retained fixed-two code is legacy swap-journal recovery, which is unreachable from the released UI.
