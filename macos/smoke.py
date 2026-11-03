"""Verify the actual frozen launchers, engine and backend bridge, without a login."""

import os
from pathlib import Path
import subprocess
import sys
import tempfile


def main(app: Path) -> None:
    app = app.resolve()
    executable = app / "Contents" / "MacOS" / "Shiftbar"
    engine = executable.with_name("cswap")
    backend = app / "Contents" / "Resources" / "backend" / "shiftbar-backend"
    with tempfile.TemporaryDirectory(prefix="shiftbar-smoke-") as directory:
        root = Path(directory)
        env = {k: v for k, v in os.environ.items() if k not in (
            "GH_TOKEN", "GITHUB_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN",
            "CLAUDE_CONFIG_DIR", "RESOURCEPATH", "PYTHONPATH", "PYTHONHOME",
        )}
        env.update(HOME=str(root), USERPROFILE=str(root), XDG_DATA_HOME=str(root / "xdg"),
                   CLAUDE_CONFIG_DIR=str(root / "claude"), SHIFTBAR_DATA=str(root / "data"))
        for command in ([engine, "--version"], [executable, "--engine", "--version"],
                        [backend, "--help"]):
            result = subprocess.run(command, env=env, text=True, capture_output=True, timeout=30)
            if result.returncode:
                raise RuntimeError(f"Frozen executable failed: {command[0].name}, exit {result.returncode}: {result.stderr}")
            print(result.stdout.strip())
        env["SHIFTBAR_SMOKE_HOME"] = str(root)
        result = subprocess.run([executable, "--native-smoke"], env=env, text=True,
                                capture_output=True, timeout=40)
        marker = "Native menu bar, backend bridge and bundled credential engine: passed"
        assert result.returncode == 0 and marker in result.stdout, (
            result.returncode, result.stdout, result.stderr,
        )
        print(marker)


if __name__ == "__main__":
    main(Path(sys.argv[1]))
