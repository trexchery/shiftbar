"""Executable desktop protocol contract, with a synthetic engine only.

Run after dotnet build with SHIFTBAR_TEST_BACKEND set to the apphost.
The same tests exercise Linux DDC and the Apple Silicon executable in CI.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

import pytest


@pytest.fixture
def host(tmp_path):
    executable = os.environ.get("SHIFTBAR_TEST_BACKEND")
    if not executable:
        pytest.skip("Set SHIFTBAR_TEST_BACKEND to the compiled backend apphost")
    engine = tmp_path / "synthetic_engine.py"
    engine.write_text('''import json, sys
if sys.argv[1] == "config":
    print(json.dumps({"schemaVersion":1,"settings":[{"key":"autoswitch.threshold","value":"90"}]}))
elif sys.argv[1] == "list":
    print(json.dumps({"schemaVersion":1,"activeAccountNumber":None,"accounts":[]}))
else:
    raise SystemExit(2)
''')
    env = dict(os.environ)
    for key in list(env):
        if key.startswith(("ANTHROPIC_", "CLAUDE_", "HANDOFF_", "PATCH_GOBLINS_", "SHIFTBAR_")):
            env.pop(key)
    env.update({"HOME": str(tmp_path), "USERPROFILE": str(tmp_path),
                "SHIFTBAR_DATA": str(tmp_path / "data"),
                "SHIFTBAR_ENGINE": sys.executable,
                "SHIFTBAR_ENGINE_ARGS": json.dumps([str(engine)])})
    process = subprocess.Popen([executable, "--serve"], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding="utf-8", env=env)
    incoming = queue.Queue()
    def read():
        for line in process.stdout:
            incoming.put(json.loads(line))
    thread = threading.Thread(target=read, daemon=True)
    thread.start()
    def send(id, method, params=None):
        process.stdin.write(json.dumps({"id": id, "method": method, "params": params or {}}) + "\n")
        process.stdin.flush()
    def receive(predicate):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            value = incoming.get(timeout=max(0.1, deadline - time.monotonic()))
            if predicate(value):
                return value
        raise AssertionError("Backend did not send the expected response")
    receive(lambda value: value.get("event") == "ready")
    yield process, send, receive, env
    if process.poll() is None:
        try:
            send(9999, "quit")
            process.wait(timeout=15)
        except (OSError, subprocess.TimeoutExpired):
            process.kill()
            process.wait(timeout=5)
    process.stdin.close()
    thread.join(timeout=5)
    process.stdout.close()
    process.stderr.close()


def test_executable_status_and_settings(host, tmp_path):
    process, send, receive, env = host
    send(1, "status")
    status = receive(lambda value: value.get("id") == 1)["result"]
    assert status["schemaVersion"] == 1
    assert "config" in status and "primeStatus" in status
    send(2, "settings", {"values": {"quotaNotifications": True, "autoProjectAccount": True, "terminalFolder": None}})
    settings = receive(lambda value: value.get("id") == 2)["result"]
    assert settings["quotaNotifications"] is True
    assert settings["autoProjectAccount"] is True and settings["terminalFolder"] is None
    assert (tmp_path / "data" / "settings.json").is_file()


def test_retired_settings_and_methods_are_refused(host):
    process, send, receive, env = host
    send(3, "settings", {"values": {"agentIssues": 3}})
    assert "Unsupported desktop setting" in receive(lambda value: value.get("id") == 3)["error"]["message"]
    for n, method in enumerate(("call", "approval", "new_agent", "handoff", "provider_switch"), start=4):
        send(n, method)
        assert "Unknown backend method" in receive(lambda value, n=n: value.get("id") == n)["error"]["message"]


def test_second_backend_cannot_open_same_state_and_empty_prime_sends_nothing(host):
    process, send, receive, env = host
    duplicate = subprocess.run([process.args[0], "--serve"], env=env, input="", capture_output=True, text=True, timeout=15)
    assert duplicate.returncode != 0
    send(12, "prime")
    assert receive(lambda value: value.get("id") == 12)["result"] == []


def test_browser_settings_and_profiles(host):
    process, send, receive, env = host
    send(20, "settings", {"values": {"browserFollowsAccount": True, "browser": "edge", "closeOldBrowserWindows": False,
                                     "browserProfile": {"email": "Synthetic@Example.com", "dir": "Profile 3"}}})
    settings = receive(lambda value: value.get("id") == 20)["result"]
    assert settings["browserFollowsAccount"] is True and settings["closeOldBrowserWindows"] is False
    assert settings["browser"] == "edge" and settings["browserProfiles"] == {"Synthetic@Example.com": "Profile 3"}
    send(21, "settings", {"values": {"browser": "netscape"}})
    assert "Unknown browser" in receive(lambda value: value.get("id") == 21)["error"]["message"]
    send(22, "browserProfiles")
    info = receive(lambda value: value.get("id") == 22)["result"]
    assert info["browser"] == "edge"
    assert isinstance(info["installed"], list) and isinstance(info["profiles"], list) and info["resolved"] == {}
