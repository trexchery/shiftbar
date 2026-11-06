<p align="center">
  <img src="assets/readme/hero.png" width="100%" alt="Shiftbar: the tray flyout lists Claude accounts with 5-hour and weekly meters and the open Claude Code sessions; a toast offers to continue a session that stopped at its limit on another account.">
</p>

<p align="center">
  <a href="https://github.com/trexchery/shiftbar/releases/latest"><img src="https://img.shields.io/github/v/release/trexchery/shiftbar?style=flat-square&color=4F5BD5&label=download" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows%2011-x64-4F5BD5?style=flat-square" alt="Windows 11 x64">
  <a href="docs/macos.md"><img src="https://img.shields.io/badge/macOS-Apple%20Silicon-4F5BD5?style=flat-square" alt="macOS Apple Silicon"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-4F5BD5?style=flat-square" alt="MIT license"></a>
  <img src="https://img.shields.io/badge/telemetry-none-4F5BD5?style=flat-square" alt="No telemetry">
</p>

<p align="center">
  <b>Stop rationing your week.</b> When one Claude account hits its limit, the others sit idle.<br>
  Shiftbar keeps all your Claude Code accounts in the tray and moves you to the one with room, without closing a session.
</p>

<p align="center">
  <img src="assets/readme/flyout.png" width="300" alt="The flyout: two accounts with 5-hour and weekly meters and a weekly pace line, and a Sessions list where one session stopped at its limit offers Continue on personal.">
  &nbsp;&nbsp;
  <img src="assets/readme/toast.png" width="420" alt="A toast: work stopped at work's limit, with a Continue on personal button.">
</p>

## Why Shiftbar

- **🔀 Switch without breaking anything.** One click, or auto-switch near a limit; an account you pick yourself stays until it runs out. A switch never closes or restarts a window, and on Windows Remote Control reconnects in idle windows afterwards.
- **🪟 Accounts side by side.** Open a terminal as any account while the others keep running. Pin a project folder to an account.
- **📊 Every limit in one place.** 5-hour and weekly meters for each account, a pace forecast, the next reset, and one `/stats` across all accounts.
- **⏭️ Carry on at a limit.** On Windows, when a session stops at its limit, one click continues it on the account with room, conversation intact.

## How it compares

|  | Shiftbar | [claude-swap](https://github.com/realiti4/claude-swap) | [Claude Usage Tracker](https://github.com/hamed-elfayome/Claude-Usage-Tracker) |
|---|:---:|:---:|:---:|
| Usage meters for several accounts | ✅ | ✅ | ✅ |
| Switches Claude logins | ✅ | ✅ | ✅ |
| Accounts side by side | ✅ | ✅ | ✅ |
| Auto-switch | ✅ near a limit | ✅ near a limit | at the limit |
| Native Windows | ✅ tray | CLI | – |

Shiftbar is built on claude-swap's credential and usage engine. Everything stays local: no proxy, no cloud.

## Quick start

**Windows 11:** needs [Claude Code](https://docs.anthropic.com/en/docs/claude-code).

1. **Download** `shiftbar-win-x64.zip` from the [latest release](https://github.com/trexchery/shiftbar/releases/latest), extract it, and run `shiftbar.exe`.
2. **Add your accounts.** Open the tray icon and use **+** to sign in to each one through the official Claude Code login.
3. **Work.** Switch from the flyout, or use **Open terminal** to run Claude Code as a specific account.

**macOS (Apple Silicon):** see [docs/macos.md](docs/macos.md).

## What's inside

<table>
<tr>
<td width="33%" valign="top">

**Accounts**

- 5-hour and weekly meters
- weekly pace and next reset
- one-click switch to the best account
- one `/stats` for all accounts

</td>
<td width="33%" valign="top">

**Sessions**

- terminal per account
- folder pins
- live Sessions panel (Windows)
- Remote Control reconnect (Windows)

</td>
<td width="33%" valign="top">

**Limits**

- auto-switch (opt-in)
- Continue on the best account (Windows)
- quota alerts (opt-in)
- Auto-Prime (opt-in)

</td>
</tr>
</table>

> [!IMPORTANT]
> **Read Anthropic's terms.** The [Consumer Terms](https://www.anthropic.com/legal/consumer-terms) and [Usage Policy](https://www.anthropic.com/legal/aup) restrict sharing credentials and automated access, and plan limits assume ordinary individual use. Auto-switch and Auto-Prime are off by default; turning them on is your call. Shiftbar is not affiliated with Anthropic.

<details>
<summary><b>Good to know</b></summary>

- **Local only.** No telemetry, no cloud sync, no proxy. Logins stay on your machine: DPAPI on Windows, the Keychain on macOS.
- **Never interrupts.** Shiftbar doesn't close or restart a window you can see. With auto-switch on, a session whose terminal has already died is reopened with `--resume` (Windows). The only thing it ever types is `/remote-control`, into an idle window with an empty prompt, while you're away from the keyboard.
- **Windows only, for now:** the Sessions panel, Continue on, reopening dead sessions with `--resume`, and Remote Control reconnect. macOS has the rest in its menu bar; [docs/macos.md](docs/macos.md) lists the gaps.
- **One `/stats`.** Usage on your other accounts is added to the "All time" view of your normal `claude` once each UTC day ends. Preview with `cswap stats-sync --dry-run`.
- **Unsigned releases**, so SmartScreen may warn. A `.sha256` checksum ships with every download. See [SECURITY.md](SECURITY.md).
- **Upgrading from Patch Goblins:** quit the old tray and run `shiftbar.exe` once. Settings, logins and autostart carry over.
- **The engine is `cswap`.** It ships in `engine\` and is documented in [docs/claude-swap.md](docs/claude-swap.md).
- **Build from source:** `pwsh dock\build.ps1` (needs [uv](https://docs.astral.sh/uv/) and the .NET 8 SDK). On macOS: `bash macos/install.sh`.

</details>

<p align="center"><sub>Built on <a href="https://github.com/realiti4/claude-swap">claude-swap</a> by Onur Cetinkol · <a href="LICENSE">MIT</a></sub></p>
