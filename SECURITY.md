# Security policy

Please report vulnerabilities privately through GitHub:
**Security → Report a vulnerability** on this repository. Don't open a public issue.

Include what you found, how to reproduce it, and what an attacker could do with it.
Never include real access tokens, refresh tokens or credential files. Use made-up values.

In scope: Shiftbar's tray and menu bar apps, the bundled claude-swap engine, the build pipeline and release artifacts.
Bugs in Claude Code itself belong with Anthropic.

## What Shiftbar keeps, and where

- Account credentials stay on your machine: sealed with DPAPI on Windows, in the Keychain on macOS, and never in Git, logs or CI output.
- There is no telemetry, cloud sync or proxy. The bundled engine talks only to Anthropic, to read usage and refresh logins.
- Releases are unsigned (macOS builds are ad hoc signed, not notarized), so SmartScreen or Gatekeeper may warn. Every Windows release ships a `.sha256` checksum: verify the zip against it before running.
