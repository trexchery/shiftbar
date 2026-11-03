# macOS verification

What the repository's workflows run for the macOS app. Pass counts and build results belong to a specific commit and workflow run: read them from the run, not from this page.

## CI jobs

- **CI, `macos-keychain`** (pull requests and pushes to `main`; docs-only changes skip it): the whole Python suite on a macOS runner, including the Keychain and menu bar tests, with synthetic credentials.
- **macOS application, `backend`** (pull requests to `main` that change more than docs, `v*` tags, manual dispatch): the shared backend suite (`dock/Shiftbar.Backend.Tests`) and `tests/test_backend_host.py`, which starts the real backend executable and checks its JSON-lines protocol, settings, refusal of retired settings and methods, and that a second backend cannot open the same state. TRX files are uploaded as `macos-verification`.
- **macOS application, `apple-silicon`**: `macos/build-macos.sh`. It publishes the backend for `osx-arm64`, builds the py2app bundle, signs it ad hoc and checks `codesign --verify --deep --strict`. Then `macos/smoke.py` runs the frozen engine and backend and starts the real menu bar app under an isolated home until it obtains an account snapshot from the backend and the bundled engine. The zip, disk image and checksums are uploaded as `shiftbar-macos-arm64` (30 days).

All of it runs on synthetic accounts and temporary homes.

## Not verified

- No real Claude credentials or inference were used. Interactive OAuth consent and a full logout and login cycle are untested.
- Interactive behavior of the menu bar, Keychain consent prompts, Terminal launching and launch at login need a person at a Mac.
- Builds are ad hoc signed. Developer ID signing and notarization need credentials the project does not have.
- Remote Control reconnect is Windows-only; there is nothing to verify on macOS.
