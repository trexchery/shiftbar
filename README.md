<p align="center">
  <img src="assets/readme/hero.png" width="100%" alt="Shiftbar: every Claude account, one tray. The tray flyout lists two accounts with 5-hour and weekly meters and the open Claude Code sessions, and a toast offers Continue on personal for a session that stopped at its limit.">
</p>

<p align="center">
  <a href="https://github.com/trexchery/shiftbar/releases/latest"><img src="https://img.shields.io/github/v/release/trexchery/shiftbar?style=flat-square&color=4F5BD5&label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows%2011-x64-4F5BD5?style=flat-square" alt="Windows 11 x64">
  <a href="docs/macos.md"><img src="https://img.shields.io/badge/macOS-Apple%20Silicon-4F5BD5?style=flat-square" alt="macOS Apple Silicon"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-4F5BD5?style=flat-square" alt="MIT license"></a>
  <img src="https://img.shields.io/badge/telemetry-none-4F5BD5?style=flat-square" alt="No telemetry">
</p>

<h3 align="center">Every Claude account, one tray.</h3>

<p align="center">
  Shiftbar is a multi-account manager for <a href="https://docs.anthropic.com/en/docs/claude-code">Claude Code</a>.<br>
  See every account's limits, move to the one with room, run several side by side, and carry on when one runs out.
</p>

## What it does

### Switching that never interrupts a session
- **One click, one keystroke.** Switch from the flyout, the tray menu or the arrow keys. **Use best** moves to the account with the most room.
- **Windows are never closed, killed or restarted.** Shiftbar moves the default login; it takes the same credential locks Claude Code uses, so a switch never collides with a token refresh.
- **Held out when you say so.** An account you exclude is skipped by auto-switch and **Use best**, and an account whose login has expired is flagged.

### Isolated concurrent sessions
- **A terminal per account.** **Open terminal** starts Claude Code as that account, in the folder you pick, while every other window stays on its own account (`cswap run`). Switching and auto-switch never touch that terminal, and each account keeps its own chat history. Opening one as the account that is already the default login runs plain `claude`, which follows later switches.
- **Folder pins.** Assign a project folder to an account; with **Use project accounts** on, a terminal opened there uses it.
- **A Sessions panel** lists the open Claude Code windows with their account, state and age, and opens a terminal in the same folder on the same account.

### Unified usage
- **5-hour and weekly meters** for every account, with each model's own weekly window where Anthropic reports one, and reset times.
- **Pace forecast.** Each row says whether the weekly window lasts to its reset ("weekly on pace", "weekly runs out in 19h").
- **Cross-account summary** in the header and the tray tooltip: "2 of 3 accounts have room, next reset in 1h".
- **One `/stats`.** Usage from your other accounts is added to the "All time" view of your normal `claude` (see below).

### Limit handling
- **Auto-switch (opt-in).** Moves the default login off an account near its limit. The switch line adapts to how fast the account is burning quota (80 to 98%), or stays at a fixed threshold you choose. A usage-limit error in any window triggers it within seconds.
- **Continue on &lt;best&gt;.** When a session stops at a limit, a toast and the Sessions panel offer to reopen it in the same folder on the account with the most room, conversation resumed. The stopped window stays open until you close it.
- **Remote Control reconnect (Windows).** A switch drops Remote Control in running windows. Shiftbar types `/remote-control` into idle windows that had it, only when the prompt is empty and you have been away from the keyboard.
- **Quota alerts (opt-in).** Notifications near a limit (80%), at it and when it resets.
- **Auto-Prime (opt-in).** Starts an account's fresh 5-hour window early with one tiny request, so it also resets early.

<p align="center">
  <img src="assets/readme/flyout.png" width="300" alt="The flyout with two synthetic accounts, a weekly pace line on each, and a Sessions list in which the top session stopped at its limit and offers Continue on personal.">
  &nbsp;&nbsp;&nbsp;
  <img src="assets/readme/toast.png" width="420" alt="A toast reading: work stopped at work's limit. Continue it on personal. With a Continue on personal button.">
</p>

## How it compares

|  | **Shiftbar** | [claude-swap](https://github.com/realiti4/claude-swap) | [Claude Usage Tracker](https://github.com/hamed-elfayome/Claude-Usage-Tracker) | [CodexBar](https://github.com/steipete/CodexBar) | [cc-switch](https://github.com/farion1231/cc-switch) |
|---|:---:|:---:|:---:|:---:|:---:|
| Platforms | Windows 11, macOS (Apple Silicon) | Windows, macOS, Linux | macOS 14+ | macOS, Linux (Windows: community ports) | Windows, macOS, Linux |
| Interface | Tray flyout, menu bar | CLI, TUI, macOS menu bar | Menu bar, notch, statusline | Menu bar, widgets, CLI | Tray app |
| Switches Claude Pro/Max logins | ✅ | ✅ | ✅ | – read-only | – API providers, not logins |
| Concurrent isolated sessions | ✅ | ✅ | ✅ launchers | – | ? |
| 5-hour and weekly usage per account | ✅ | ✅ | ✅ | ✅ | ? |
| Auto-switch near a limit | ✅ opt-in | ✅ | ✅ | – | – provider failover only |
| Reopen a stopped session on another account | ✅ Windows | ? | ? | – | – |
| Reconnect Remote Control after a switch | ✅ Windows | ? | ? | – | – |
| Where usage comes from | Claude's OAuth usage endpoint | same (it is the engine) | claude.ai endpoint (session cookie) and CLI OAuth | OAuth, cookies, CLI fallback | local logs |
| Native Windows tray | ✅ | – | – | – | ✅ |

<sub>Compiled 2026-11-03 from each project's README and release notes, which change. **?** means the project's own docs don't say. Shiftbar is built on claude-swap's engine, so everything `cswap` does stays available.</sub>

## Quick start

**Windows 11 (x64)**

1. Install [Claude Code](https://docs.anthropic.com/en/docs/claude-code).
2. Download `shiftbar-win-x64.zip` from the [latest release](https://github.com/trexchery/shiftbar/releases/latest), extract it and run `shiftbar.exe`. The tray icon may sit in the overflow area; click it to open the flyout.
3. Click **+** to add an account. A console opens with the official Claude Code login; repeat for each account.
4. Use **Open terminal** on an account to run Claude Code as it, or switch the default login from the flyout.

Releases are unsigned, so SmartScreen may warn. A `.sha256` checksum ships with every download (see [SECURITY.md](SECURITY.md)).

**macOS (Apple Silicon, 12+):** a native menu bar app with switching, usage, auto-switch, quota alerts, Auto-Prime and per-account terminals. Install and gaps against Windows: [docs/macos.md](docs/macos.md).

## Privacy and security

- **Local only.** No account, no cloud sync, no telemetry, and no proxy: your requests go from Claude Code to Anthropic as they always did. The engine talks only to Anthropic, to read usage and to refresh logins.
- **Credentials stay on your machine and out of Git.** Backups are sealed with DPAPI on Windows and kept in the Keychain on macOS. Logs redact secrets.
- **Never in the way.** Shiftbar does not close, kill or restart a Claude Code window. Remote Control reconnect is the one thing it ever types, and only into an idle window with an empty prompt.
- **Auditable.** MIT licensed; the credential and switching engine is [claude-swap](https://github.com/realiti4/claude-swap)'s. Report vulnerabilities as described in [SECURITY.md](SECURITY.md).

## Anthropic's terms

Shiftbar only moves your own logins around on your own machine. It does not share accounts, proxy requests or hide who you are. Still, read Anthropic's [Consumer Terms](https://www.anthropic.com/legal/consumer-terms), [Usage Policy](https://www.anthropic.com/legal/aup) and [Claude Code legal page](https://code.claude.com/docs/en/legal-and-compliance) yourself: they restrict sharing credentials and automated access, and plan limits assume ordinary individual use. **Auto-switch and Auto-Prime are off by default.** Turning them on is your call and your risk. Shiftbar is an independent project, not affiliated with or endorsed by Anthropic.

## Good to know

- **One `/stats` for all accounts.** Usage on your other accounts (terminals, Auto-Prime, reopened windows) is added to the "All time" view of your normal `claude` once each UTC day ends. Only `~/.claude/stats-cache.json` is written; the 7-day and 30-day tabs still show your default login only. Preview with `cswap stats-sync --dry-run`.
- **Terminals Shiftbar opens** carry `--remote-control` when the last session in that folder used it (the **Preserve Remote Control** setting, on by default).
- **The engine is `cswap`.** `engine\cswap.exe` ships in the zip; its documentation is [docs/claude-swap.md](docs/claude-swap.md).

## Upgrading from Patch Goblins

Shiftbar is Patch Goblins 0.6 pivoted into a pure account manager. The autonomous agents, Codex and Grok meters and handoff are gone; switching, sessions, usage and limit handling are what is left, and they work as before.

1. Quit the old tray (Shiftbar refuses to start while it runs, and says so).
2. Run `shiftbar.exe` once. Your settings carry over, your accounts and logins are untouched, and launch at login stays on if it was. Retired agent settings are ignored.
3. Delete the old install folder when you like. Installs of handoff, Autodock and ClautoSwap upgrade the same way.

`PATCH_GOBLINS_*` environment variables are still read; the new names are `SHIFTBAR_*`.

<details>
<summary><b>Build from source</b></summary>

Requires [uv](https://docs.astral.sh/uv/), the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and PowerShell 7.

```powershell
git clone https://github.com/trexchery/shiftbar.git
cd shiftbar
pwsh dock\build.ps1          # engine + tray + both test suites -> dist\shiftbar-win-x64.zip
```

On Apple Silicon: `bash macos/install.sh`. Tests: `uv run pytest -q` and `dotnet test dock/Shiftbar.Tests/Shiftbar.Tests.csproj -c Release`. They use synthetic credentials only.

</details>

<p align="center"><sub>Built on <a href="https://github.com/realiti4/claude-swap">claude-swap</a> by Onur Cetinkol · Shiftbar by trexchery · <a href="LICENSE">MIT</a></sub></p>
<p align="center"><sub>Independent project, not affiliated with Anthropic.</sub></p>
