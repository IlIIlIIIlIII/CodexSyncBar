# Codex SyncBar

Codex SyncBar is a personal macOS menu-bar app that keeps multiple ChatGPT/Codex accounts synchronized across this Mac and user-configured SSH devices.

The fixed `368 × 720` popover shows the selected account's full email, Codex weekly and five-hour limits, Spark weekly limits, reset-credit expiry countdowns, and a compact device summary. It has no internal scroll view. Account and device management lives in a separate settings window.

## Settings

- **Accounts:** add any number of accounts, show full emails, drag to reorder, refresh, log in, log out, and remove logged-out entries. Reordering changes display order only; auth files and Chromium sessions never move.
- **SSH Devices:** add/edit/remove devices, choose private key with an optional OpenSSH certificate and passphrase, or use an SSH password. A new device is saved disabled; **Install & Enable** atomically installs the matching helper, enrolls every registered account, verifies the active account, and only then includes it in global switching. Fresh installations start with no remote devices.
- **General:** refresh the selected account or all accounts, force auth synchronization, and toggle launch at login.

Every account keeps a stable positive ID. Its canonical auth is `~/.local/share/gpt-switch/profiles/<id>.auth.json`, and its persistent Google Chrome profile is `~/Library/Application Support/Codex SyncBar/ChromeProfiles/profile-<id>`. Adding, deleting, or reordering another account never changes these paths.

Authentication opens in an app-managed Google Chrome profile. The profiles have independent persistent Chrome storage, so Google sessions survive app upgrades while signed Chrome provides passkey and Touch ID support. Google Chrome must be installed in `/Applications`.

## Security boundaries

- OAuth tokens stay in permission-`0600` Codex/profile auth files. They are never copied to preferences or logs.
- `~/.local/share/gpt-switch/config.json` contains only account labels/order and non-secret SSH metadata; it is atomically stored with mode `0600`.
- SSH passwords and key passphrases are generic-password items in macOS Keychain under service `com.sunggu.codexsyncbar.ssh`. Each endpoint uses a random credential namespace; changing the host, user, port, key, certificate, or authentication method rotates that namespace before the old secret is removed. Secrets are never stored in JSON, argv, or logs.
- Private-key and certificate paths must be absolute, regular, non-symlink files owned by the current user. Private keys must have mode `0400` or `0600`.
- SSH keeps strict host-key checking and disables agent, X11, port forwarding, and TTY allocation. Password/passphrase input uses the bundled askpass helper.
- The Mac stores the only full refresh credentials. SSH replicas receive access-only auth files with an empty `refresh_token`.
- Account switching stops only the remote Codex `app-server proxy` and Unix-socket `app-server --listen` processes that can cache the previous credential. The Mac app then reconnects and starts them with the newly installed auth; unrelated Codex CLI jobs and non-Unix app servers are left running.
- A login is validated as a full Codex-managed credential and checked against every registered account before atomic promotion. A failed or cancelled first login does not overwrite an existing account.
- Logout requires a different authenticated fallback account, moves all configured devices to it, and then transactionally removes the selected credential. Logout is available only in settings.
- Browser cookies and token validity are separate. Updates and transient errors do not erase either, but upstream logout, security changes, administrator revocation, or a revoked refresh token can still require login.

## Build and install

```bash
chmod +x build-app.sh Tests/helper-contract-tests.sh
bash Tests/helper-contract-tests.sh
swift test
./build-app.sh
```

The build embeds matching `gpt-switch` and `codex-syncbar-askpass` resources. Runtime installs them as:

- `~/.local/bin/gpt-switch`
- `~/.local/lib/gpt-switch/codex-syncbar-askpass`

The helper is installed with mode `0755`; the Keychain askpass bridge is owner-only mode `0700`.

Every SSH replica must run the same `gpt-switch --version` as the app. The output app is ad-hoc signed for this Mac; distributing it to other Macs requires Developer ID signing and notarization.

## Data-source note

The usage adapter follows the current Codex-compatible response shape and is isolated in `UsageService.swift` because the ChatGPT usage backend is not a public stable API contract. The login process uses the installed official Codex CLI.
