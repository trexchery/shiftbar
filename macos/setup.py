"""Build with `bash macos/build-macos.sh`; no alias/system Python dependency."""

from pathlib import Path
from setuptools import setup


ROOT = Path(__file__).resolve().parent.parent
setup(
    name="Shiftbar",
    version="0.8.1",
    app=[str(ROOT / "macos" / "main.py")],
    options={
        "py2app": {
            "arch": "arm64",
            "argv_emulation": False,
            "no_chdir": True,
            "packages": ["claude_swap", "textual", "rumps", "truststore"],
            "includes": ["AppKit", "Foundation", "objc"],
            "excludes": ["tkinter", "pytest", "setuptools", "pip"],
            "extra_scripts": [str(ROOT / "macos" / "cswap.py")],
            "iconfile": str(ROOT / "build" / "macos" / "shiftbar.icns"),
            "resources": [
                str(ROOT / "assets" / "shiftbar.png"),
                str(ROOT / "LICENSE"),
            ],
            "plist": {
                "CFBundleName": "Shiftbar",
                "CFBundleDisplayName": "Shiftbar",
                "CFBundleIdentifier": "com.shiftbar.app",
                "CFBundleShortVersionString": "0.8.1",
                "LSUIElement": True,
                "LSMinimumSystemVersion": "12.0",
                "NSHighResolutionCapable": True,
                "NSHumanReadableCopyright": "Shiftbar contributors; claude-swap © Onur Cetinkol, MIT",
            },
        }
    },
)
