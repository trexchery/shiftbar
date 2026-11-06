# Shiftbar on macOS

Shiftbar for macOS is a menu bar app for Apple Silicon (macOS 12 or later). It combines the claude-swap Python engine and its menu bar (accounts, switching, usage) with the same C# upkeep code the Windows tray uses for auto-switch, quota alerts and Auto-Prime. Windows Forms stays in the Windows project; the macOS backend links the shared sources directly, so there is one implementation of those policies.

Claude Code is the only external tool. An installed app bundles its own Python runtime and a self-contained .NET backend, so it needs no system Python or .NET.

## Install

From a source checkout on an Apple Silicon Mac:

```bash
bash macos/install.sh
```

The installer uses Homebrew to obtain missing build tools, builds the app, checks the archive checksum and the app signature, and installs `Shiftbar.app` under `~/Applications`. An earlier install is kept under a timestamped name. `SHIFTBAR_INSTALL_DIR` selects another destination.

To install a downloaded CI archive, extract the GitHub Actions artifact so the application zip and its `.sha256` file sit side by side, then run:

```bash
bash macos/install.sh /absolute/path/shiftbar-macos-arm64.zip
```

The [macOS application workflow](https://github.com/trexchery/shiftbar/actions/workflows/macos.yml) uploads the `shiftbar-macos-arm64` artifact (application zip, disk image, checksums) after its build job succeeds. It runs on pull requests to `main` that change more than docs, on `v*` tags and on manual dispatch; artifacts are kept for 30 days and are available to users with access to the repository. Tagged releases on GitHub carry the Windows build only.

Builds are ad hoc signed, not notarized (that needs a Developer ID and Apple credentials), so Gatekeeper may ask about a downloaded build. The installer does not disable Gatekeeper or clear quarantine attributes.

## Upgrading from Patch Goblins

Quit the old app first (its menu bar, **Quit**); Shiftbar refuses to start while it runs, and says so in an alert. Opening Shiftbar once then moves `~/Library/Application Support/patch-goblins` to `shiftbar` and, if launch at login was on, replaces the old login item with `com.shiftbar.app`. Accounts and logins live in the engine's own store and Keychain entries, which never moved.

## Use

Launch **Shiftbar** from Applications. The menu bar title shows the active account's usage. Its menu lists every account (click one to switch), with **Rotate to next**, **Switch to best**, **Next available**, add, remove, disable and enable, switch history, and the engine's own **Settings** (title, refresh interval, **Auto-switch accounts**).

The **Shiftbar** submenu adds:

- **Open account dashboard**: the engine's interactive dashboard in Terminal.
- **Sign in another account…**: the official Claude Code login in an isolated profile, then the account is added.
- **Prime fresh 5-hour windows** and **Auto-Prime status**: start fresh windows early with one tiny request, now or automatically.
- **Account tools**: for each account, **Open terminal…** (Claude Code as that account, in a folder you pick), **Set alias…** and **Assign folder…**, plus removing and viewing folder mappings.
- **Settings**: Auto-Prime, Quota notifications, Restore Remote Control, Prefer folder account, and the folder terminals start in.
- **Launch at login**: registers a launchd LaunchAgent.

Auto-switch, quota notifications and Auto-Prime are off by default. A terminal opened as an account stays on that account through switches; with **Restore Remote Control** on, it starts with `--remote-control` when the last session in that folder used it.

The backend is started by the app. Its executable only answers:

```bash
"$HOME/Applications/Shiftbar.app/Contents/Resources/backend/shiftbar-backend" --version
"$HOME/Applications/Shiftbar.app/Contents/Resources/backend/shiftbar-backend" --help
```

## State and recovery

Desktop settings, logs and Auto-Prime records live under `~/Library/Application Support/shiftbar`. The engine keeps its store under `~/.claude-swap-backup` and uses the macOS Keychain. Both stay outside Git. `SHIFTBAR_DATA` selects a separate, isolated desktop instance for development or synthetic testing (the old `PATCH_GOBLINS_*` names still work).

The backend takes a private instance lock and runs on a single-thread synchronization context. State writes are atomic. Quitting the app stops the backend; if the backend stops on its own the menu shows "Shiftbar (stopped)" with **Restart backend**, and the app also restarts it a few times by itself.

## Security boundaries

- Account credentials stay in the engine and the macOS Keychain. The protocol between the menu bar and the backend carries account status and requests, never credential exports.
- The menu bar app makes no network calls of its own: usage and logins go through the engine to Anthropic. No telemetry, no proxy.
- Logs and diagnostics pass through the shared secret redactor. Tests use synthetic credentials.

## Gaps against the Windows tray

- **No Sessions panel and no Continue on.** The Windows tray lists live Claude Code windows and reopens a stopped one on an account with room. On macOS, with auto-switch on, a session stopped at its limit raises a "Terminal needs attention" notice when another account has room; the session stays open and you continue it yourself.
- **Remote Control is not reconnected after a switch (Windows only).** A switch of the default login drops Remote Control in running Claude Code windows. On Windows the tray types `/remote-control` into idle ones (`dock/Shiftbar/RemoteControl.cs`, `ConsoleNudge.cs`); macOS has no equivalent yet (it would need the Accessibility API to read and type into Terminal). Type `/remote-control` in each window yourself.
- **Exclusive switching is automatic only.** Automatic switches never move the default login onto an account another `cswap run` terminal is holding, because on macOS nothing keeps the two logins in step. A manual switch from the menu to such an account only warns.
- **No consent dialog.** The Windows tray asks you to acknowledge Anthropic's terms the first time you turn on auto-switch; the macOS menu does not. The README's terms note applies all the same.
- **Two settings are file-only.** The backend poll interval (`refreshSeconds` in `settings.json`) and the switch delay (`cswap config`) have no macOS menu entry.
- **Menu bar menus, not a flyout.** Usage meters and pace lines are the engine's menu bar and dashboard, not the Windows flyout.

## Build and verification

```bash
bash macos/build-macos.sh
```

It uses the locked Python dependencies, publishes the backend for `osx-arm64`, builds the standalone py2app bundle, signs it ad hoc, runs the executable smoke checks and writes `shiftbar-macos-arm64.zip`, `.dmg` and `.sha256` files under `dist/`.

What CI verifies, and what it does not, is in [macos-verification.md](macos-verification.md).
