# Codex SyncBar Settings, Multi-Account, and SSH Device Design

Date: 2026-07-14

## Objective

Move every management action out of the fixed-height menu-bar popover and into a dedicated macOS settings window. Support any practical number of Codex accounts and SSH devices without renaming or copying existing authentication state.

The user explicitly requested autonomous design, implementation, and verification, so this document records the selected defaults instead of pausing for design approval.

## Approaches Considered

### 1. Stable account IDs plus a persisted registry — selected

Keep numeric account IDs as permanent storage identities. `profiles/<id>.auth.json` and Chromium `profile-<id>` remain untouched. A versioned configuration stores full email labels and display order separately. Reordering changes only the array order.

This preserves the two existing accounts and browser sessions, extends naturally to account 3 and beyond, and lets the current distributed switch transaction remain the synchronization engine.

### 2. Rewrite account and SSH orchestration in Swift

This would make typed process control and Keychain integration cleaner, but it would replace a mature shell transaction implementation in the same release as the UI migration. The rollback and remote-partial-failure risk is too high.

### 3. Keep two fixed slots and put a settings facade on top

This is the smallest UI patch, but it cannot satisfy more than two accounts or user-defined SSH devices. It is rejected.

## Data Model and Persistence

The canonical file is `~/.local/share/gpt-switch/config.json`. Its containing directory is mode `0700`; the regular, non-symlink file is mode `0600` and is replaced atomically.

```json
{
  "schemaVersion": 1,
  "nextAccountID": 3,
  "accounts": [
    { "id": 1, "email": "alice@example.com" },
    { "id": 2, "email": "bob@example.com" }
  ],
  "devices": [
    {
      "id": "build-server",
      "displayName": "Build Server",
      "host": "build.example.internal",
      "port": 22,
      "username": "alice",
      "authentication": "openSSHConfig",
      "identityFile": null,
      "certificateFile": null,
      "hasPassword": false,
      "hasKeyPassphrase": false,
      "enabled": true
    }
  ]
}
```

Account IDs are positive, monotonically increasing, and never reused. Account email is the full email returned by the authenticated credential; the UI never uses “메인/서브” aliases. Existing files `1.auth.json`, `2.auth.json`, `current`, and Chromium directories are not renamed, copied, or rewritten during migration.

Device IDs are immutable safe slugs. Display names are user-editable. The local Mac is implicit and cannot be removed. Fresh installations start without remote devices; users add and validate each SSH endpoint from settings.

## Secrets and SSH Authentication

Passwords and private-key passphrases are never written to JSON, command-line arguments, logs, or SSH target strings. They are stored as generic passwords in the macOS Keychain:

- service: `com.sunggu.codexsyncbar.ssh`
- account: `<device-id>.password` or `<device-id>.passphrase`
- accessibility: only while this device is unlocked
- synchronization: disabled

Supported authentication modes are:

- existing OpenSSH configuration, for migrated devices only
- private key, with optional OpenSSH certificate and optional key passphrase
- SSH password

Private key and certificate paths must be absolute regular files and may not be symlinks. Host, username, port, and device ID are strictly validated before save and before execution. SSH keeps strict host-key checking and disables agent/forwarding/TTY behavior. An app-bundled askpass helper retrieves one named secret from Keychain and writes only that secret to stdout when SSH prompts. SSH receives the secret through the askpass pipe, never through argv.

## Account Lifecycle

Adding an account reserves the next stable ID and starts the existing profile-specific Chromium login. A cancelled or failed first login removes the empty reservation. A successful login stores the full email after the helper verifies that its OpenAI account ID does not duplicate any registered account.

Logging out is available only in settings. It requires another authenticated account as an explicit fallback, performs the distributed logout transaction first, clears the selected account's managed Chromium session, and retains the account row so it can be logged in again. Removing a logged-out account deletes only that registry entry and its empty browser profile. The final authenticated account cannot be logged out because all nodes require a valid active fallback.

Drag reordering changes only the account array. It never swaps auth files, browser profiles, or the active account marker. The old swap UI is removed; its recovery code remains only to finish an already-existing legacy journal safely.

## User Interface

### Menu-bar popover

The popover remains exactly `368 × 720` and contains no scroll view.

- a compact account menu displays the selected account's full email and lists every registered account
- usage and reset-expiry cards remain on the main surface
- device summary displays the local Mac and up to three remote devices, followed by an “외 N대” summary when needed
- the only management action is “설정…”; quit remains available
- refresh, login, launch at login, add account/device, reorder, and logout are absent

### Settings window

A normal `Window` scene opens at approximately `760 × 580` and uses a `NavigationSplitView` with three sections:

- Accounts: full email list, drag reorder, add, per-account login/refresh/logout/remove
- Devices: device list, add/edit/remove, SSH authentication fields, Keychain-backed password/passphrase fields, connection test
- General: refresh current/all, launch-at-login toggle and status

The settings window can scroll internally; the menu-bar popover never does.

## Synchronization and Compatibility

The shell helper remains the sole mutation boundary for distributed auth state. It loads accounts and devices from the versioned configuration, accepts every positive account ID, checks duplicates against every auth file, and iterates dynamic device arrays. Human-readable `status` remains compatible, while `status-json` provides stable structured output to Swift.

The app and helper are released together as version `2.0.0`. The build embeds the matching helper and askpass binary/script, and deployment installs those same bytes to the local helper path before launching the app. A helper version mismatch is a hard failure.

## Failure Handling

- Configuration decoding, unsafe permissions, symlinks, duplicate IDs, and invalid fields fail closed without modifying auth state.
- Account registration is atomic. A failed new login does not leave a ghost account.
- Device connection failure leaves the saved device disabled until the user fixes and tests it.
- Switch and logout transactions snapshot the enabled device IDs at start; edits are disabled while a transaction is running.
- Remote partial failure continues to use the existing transaction rollback and recovery journals.
- Existing legacy swap journals are reconciled before registry migration.

## Verification Contract

Automated tests must prove:

- lossless migration of existing accounts 1 and 2 and three SSH devices
- three-or-more account listing, selection, refresh, duplicate rejection, reorder, login, and explicit-fallback logout
- reorder leaves auth file hashes, active marker, and Chromium paths unchanged
- dynamic zero/one/many remote device status and transaction behavior
- SSH validation and exact safe argv for each authentication mode
- no plaintext password/passphrase in configuration, argv, environment diagnostics, or logs
- popover has no scroll view or management actions; settings owns all requested actions
- helper and app version parity

Live verification must build and test the release bundle, preserve current account state, test all three SSH hosts, install the app, launch it, inspect both rendered windows, and confirm the helper reports the same selected account on all reachable nodes.

## Self-Review

The design satisfies the full requested feature set without changing existing auth files or Chromium profile identities. It deliberately separates display order from credential identity, which removes the failure mode caused by the old swap operation. The security boundary is explicit: JSON contains only non-secret metadata, Keychain contains secrets, and SSH receives secrets only through askpass. The fixed popover can remain scroll-free because unbounded account and device management moves to a separately scrollable window.

The repository is not a Git worktree, so design and implementation checkpoints are recorded as files and test output rather than commits.
