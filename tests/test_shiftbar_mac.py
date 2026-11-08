"""Synthetic desktop transport/lifecycle tests, with no account access."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import queue
import time
import types
from unittest.mock import MagicMock, patch

import pytest

from claude_swap import shiftbar_mac
from claude_swap.exceptions import ClaudeSwitchError, CredentialReadError


FAKE_HOST = r'''
import json, sys
for line in sys.stdin:
    m = json.loads(line)
    if m['method'] == 'crash':
        sys.exit(7)
    elif m['method'] == 'quit':
        print(json.dumps({'id':m['id'],'result':True}), flush=True)
        break
    else:
        print(json.dumps({'event':'notify','title':'synthetic','body':'before the answer'}), flush=True)
        print(json.dumps({'id':m['id'],'result':{'schemaVersion':1,'accounts':None}}), flush=True)
'''


@pytest.fixture
def client():
    value = shiftbar_mac.BackendClient([sys.executable, "-u", "-c", FAKE_HOST])
    value.start()
    try:
        yield value
    finally:
        value.close()


def test_events_arrive_beside_answers(client):
    assert client.request("status", timeout=8)["schemaVersion"] == 1
    assert client.events.get(timeout=5)["event"] == "notify"


def test_backend_crash_unblocks_waiting_request_and_reports_stop(client):
    with pytest.raises(ClaudeSwitchError, match="backend stopped"):
        client.request("crash", timeout=8)
    assert client.events.get(timeout=5)["event"] == "stopped"


def test_backend_close_waits_for_clean_shutdown(client):
    process = client.process
    client.close()
    assert process.poll() == 0


def test_frozen_launcher_uses_bundled_engine_and_python_dispatch(monkeypatch, tmp_path):
    binary = tmp_path / "Contents" / "MacOS" / "Shiftbar"
    binary.parent.mkdir(parents=True)
    (binary.parent / "cswap").touch()
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "executable", str(binary))
    for name in ("SHIFTBAR_ENGINE", "SHIFTBAR_ENGINE_ARGS", "SHIFTBAR_PYTHON", "SHIFTBAR_PYTHON_ARGS"):
        monkeypatch.delenv(name, raising=False)
    env = shiftbar_mac.child_environment()
    assert env["SHIFTBAR_ENGINE"] == str(binary.parent / "cswap")
    assert json.loads(env["SHIFTBAR_PYTHON_ARGS"]) == ["--python-dispatch"]
    assert env["SHIFTBAR_PYTHON"] == str(binary)


def test_frozen_launcher_keeps_an_old_prefix_override_with_its_own_args(monkeypatch, tmp_path):
    binary = tmp_path / "Contents" / "MacOS" / "Shiftbar"
    binary.parent.mkdir(parents=True)
    (binary.parent / "cswap").touch()
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "executable", str(binary))
    for prefix in shiftbar_mac.ENV_PREFIXES:
        for name in ("ENGINE", "ENGINE_ARGS", "PYTHON", "PYTHON_ARGS"):
            monkeypatch.delenv(prefix + name, raising=False)
    monkeypatch.setenv("PATCH_GOBLINS_ENGINE", "/dev/python")
    monkeypatch.setenv("PATCH_GOBLINS_ENGINE_ARGS", '["-m", "claude_swap"]')
    env = shiftbar_mac.child_environment()
    assert "SHIFTBAR_ENGINE" not in env and "SHIFTBAR_ENGINE_ARGS" not in env  # the backend reads the user's pair
    assert env["SHIFTBAR_PYTHON"] == str(binary)


def test_old_module_name_still_starts_shiftbar():
    from claude_swap import goblins
    assert goblins.main is shiftbar_mac.main  # a Patch Goblins login item runs `python -m claude_swap.goblins`


def test_engine_dispatch_happens_without_loading_native_ui(monkeypatch):
    monkeypatch.setattr(sys, "argv", ["original"])
    with patch("claude_swap.cli.main") as engine:
        shiftbar_mac.main(["--engine", "list", "--json"])
    engine.assert_called_once_with()
    assert sys.argv == ["cswap", "list", "--json"]


def test_native_smoke_refuses_unisolated_account_stores(monkeypatch):
    monkeypatch.setattr(sys, "platform", "darwin")
    monkeypatch.delenv("SHIFTBAR_SMOKE_HOME", raising=False)
    with patch("claude_swap.menubar.run") as native:
        with pytest.raises(SystemExit) as failure:
            shiftbar_mac.main(["--native-smoke"])
    assert failure.value.code == 1
    native.assert_not_called()


def test_backend_auto_switch_setting_updates_menu_without_rewriting_backend(monkeypatch, tmp_path):
    from claude_swap.menubar import MenuBarSettings
    monkeypatch.setitem(sys.modules, "rumps", types.SimpleNamespace())
    client = MagicMock()
    client.events = queue.Queue()
    with patch("claude_swap.shiftbar_mac.BackendClient", return_value=client):
        extension = shiftbar_mac.MenuExtension()
    extension._last_refresh = time.monotonic()
    extension.snapshot = {"config": {"autoSwitch": True}}
    settings = MenuBarSettings(auto_switch_enabled=False)
    settings.save = MagicMock(wraps=settings.save)
    app = types.SimpleNamespace(settings=settings,
                                switcher=types.SimpleNamespace(backup_dir=tmp_path),
                                rebuild_menu=MagicMock())
    client.events.put({"event": "ui_changed"})
    extension.tick(app)
    assert settings.auto_switch_enabled is True
    assert json.loads((tmp_path / "menubar_settings.json").read_text())["auto_switch_enabled"] is True
    settings.save.assert_called_once()
    client.request.assert_not_called()
    client.events.put({"event": "ui_changed"})
    extension.tick(app)
    settings.save.assert_called_once()  # an unchanged backend snapshot does not churn the file


def test_desktop_settings_offer_only_account_settings():
    class Item:
        def __init__(self, title, callback=None):
            self.title = title
            self.callback = callback
            self.children = []

        def add(self, item):
            self.children.append(item)

    with patch("claude_swap.shiftbar_mac.BackendClient", return_value=MagicMock()):
        extension = shiftbar_mac.MenuExtension()
    extension.snapshot = {"config": {"moveWorkflows": True, "agentIssues": 3}}
    menu = extension._desktop_settings(types.SimpleNamespace(MenuItem=Item))
    titles = [item.title for item in menu.children]
    assert "Move running workflows" not in titles
    assert "Auto-Prime" in titles
    assert not any("pull request" in t.lower() or "issues" in t.lower() or "approval" in t.lower() for t in titles)


class _Item:
    def __init__(self, title, callback=None):
        self.title = title
        self.callback = callback
        self.children = []
        self.state = 0

    def add(self, item):
        self.children.append(item)


def _browser_extension():
    client = MagicMock()
    with patch("claude_swap.shiftbar_mac.BackendClient", return_value=client):
        extension = shiftbar_mac.MenuExtension()
    extension._background = lambda fn: fn()
    extension._refresh = MagicMock()
    extension.browser = {"browser": "chrome-canary",
                         "installed": [{"id": "chrome", "name": "Google Chrome"}, {"id": "chrome-canary", "name": "Google Chrome Canary"}],
                         "profiles": [{"dir": "Default", "name": "a@example.com", "userName": ""},
                                      {"dir": "Profile 2", "name": "Work", "userName": "w@example.com"}],
                         "resolved": {"A@example.com": "Default", "b@example.com": "Profile 2"}}
    return extension, client


def test_browser_settings_offer_installed_browsers_and_save_the_choice():
    extension, client = _browser_extension()
    extension.snapshot = {"config": {"browserFollowsAccount": True}}
    menu = extension._desktop_settings(types.SimpleNamespace(MenuItem=_Item))
    toggles = {item.title: item for item in menu.children}
    assert toggles["Open each account's browser profile"].state == 1
    assert toggles["Close the old account's browser windows"].state == 1  # on by default
    browsers = toggles["Browser"].children
    assert [(b.title, b.state) for b in browsers] == [("Google Chrome", 0), ("Google Chrome Canary", 1)]
    browsers[0].callback(None)
    client.request.assert_called_once_with("settings", {"values": {"browser": "chrome"}})
    extension.browser = {}
    menu = extension._desktop_settings(types.SimpleNamespace(MenuItem=_Item))
    assert [b.title for b in next(i for i in menu.children if i.title == "Browser").children] == ["No supported browser found"]


def test_account_browser_profile_menu_marks_auto_or_the_pick_and_saves_overrides():
    extension, client = _browser_extension()
    extension.snapshot = {"config": {"browserFollowsAccount": True, "browserProfiles": {"B@EXAMPLE.COM": "profile 2"}}}
    app = types.SimpleNamespace(snapshot={"accounts": [(1, "a@example.com", True, "", None, None, False, None),
                                                       (2, "b@example.com", False, "", None, None, False, None)]})
    menu = extension._account_tools(app, types.SimpleNamespace(MenuItem=_Item))
    first, second = (next(c for c in account.children if c.title == "Browser profile") for account in menu.children[:2])
    assert [(c.title, c.state) for c in first.children] == [("Auto: match email (a@example.com)", 1), ("a@example.com", 0), ("Work · w@example.com", 0)]
    assert [(c.title, c.state) for c in second.children] == [("Auto: match email", 0), ("a@example.com", 0), ("Work · w@example.com", 1)]
    first.children[2].callback(None)
    client.request.assert_called_with("settings", {"values": {"browserProfile": {"email": "a@example.com", "dir": "Profile 2"}}})
    second.children[0].callback(None)
    client.request.assert_called_with("settings", {"values": {"browserProfile": {"email": "b@example.com", "dir": None}}})


def test_account_browser_profile_menu_is_hidden_while_off_or_without_profiles():
    extension, _client = _browser_extension()
    app = types.SimpleNamespace(snapshot={"accounts": [(1, "a@example.com", True, "", None, None, False, None)]})
    def titles():
        return [c.title for c in extension._account_tools(app, types.SimpleNamespace(MenuItem=_Item)).children[0].children]
    extension.snapshot = {"config": {}}
    assert "Browser profile" not in titles()
    extension.snapshot = {"config": {"browserFollowsAccount": True}}
    assert "Browser profile" in titles()
    extension.browser = {}
    assert "Browser profile" not in titles()


def test_terminal_command_quotes_paths_and_never_uses_shell(monkeypatch, tmp_path):
    monkeypatch.setattr(sys, "platform", "darwin")
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    folder = tmp_path / "a folder's $dir"
    folder.mkdir()
    with patch("claude_swap.shiftbar_mac.subprocess.run") as opened:
        script = shiftbar_mac.run_terminal(["/app/My Apps/Shiftbar", "--engine", "run", "2"], cwd=str(folder))
    content = script.read_text()
    assert "exec '/app/My Apps/Shiftbar' --engine run 2" in content
    assert 'rm -- "$0"' in content
    assert "'\"'\"'" in content
    opened.assert_called_once_with(["/usr/bin/open", "-a", "Terminal", str(script)], check=True)


def test_terminal_script_of_an_isolated_instance_stays_in_its_data_folder(monkeypatch, tmp_path):
    monkeypatch.setattr(sys, "platform", "darwin")
    monkeypatch.setattr(Path, "home", lambda: tmp_path / "home")
    monkeypatch.setenv("SHIFTBAR_DATA", str(tmp_path / "iso"))
    with patch("claude_swap.shiftbar_mac.subprocess.run"):
        script = shiftbar_mac.run_terminal(["/app/Shiftbar", "--engine", "list"])
    assert script.parent == tmp_path / "iso" / "terminal"
    assert not (tmp_path / "home").exists()


def test_macos_enrollment_reads_keychain_profile_without_plaintext_seed(tmp_path):
    from claude_swap.enroll import build_envelope
    (tmp_path / ".claude.json").write_text(json.dumps({"oauthAccount": {"emailAddress": "synthetic@example.com"}}))
    credentials = json.dumps({"claudeAiOauth": {"accessToken": "SYNTHETIC-access", "refreshToken": "SYNTHETIC-refresh"}})
    with patch("claude_swap.enroll.read_config_dir_credentials", return_value=credentials) as read:
        envelope = build_envelope(tmp_path, 1)
    read.assert_called_once_with(str(tmp_path), strict_keychain=True)
    assert envelope["accounts"][0]["email"] == "synthetic@example.com"
    assert not (tmp_path / ".credentials.json").exists()


def test_macos_enrollment_refuses_unreadable_keychain_even_if_seed_exists(tmp_path):
    from claude_swap.enroll import build_envelope
    (tmp_path / ".credentials.json").write_text("stale")
    with patch("claude_swap.enroll.read_config_dir_credentials", side_effect=CredentialReadError("locked")):
        with pytest.raises(CredentialReadError, match="locked"):
            build_envelope(tmp_path, 1)


def test_official_login_is_isolated_and_secret_overrides_removed(monkeypatch, temp_home):
    captured = {}
    monkeypatch.setenv("ANTHROPIC_API_KEY", "SYNTHETIC-api-key")
    def login(argv, **kwargs):
        captured.update(kwargs["env"])
        assert argv == ["/fake/claude", "auth", "login"]
        assert "ANTHROPIC_API_KEY" not in kwargs["env"]
        return subprocess.CompletedProcess(argv, 0)
    with patch("claude_swap.shiftbar_mac.shutil.which", return_value="/fake/claude"), \
         patch("claude_swap.shiftbar_mac.subprocess.run", side_effect=login), \
         patch("claude_swap.enroll.enroll", return_value="synthetic@example.com") as enroll:
        assert shiftbar_mac.add_account() == "synthetic@example.com"
    assert captured["CLAUDE_CONFIG_DIR"] == captured["CLAUDE_SECURESTORAGE_CONFIG_DIR"]
    assert not Path(captured["CLAUDE_CONFIG_DIR"]).exists()
    assert enroll.call_args.args[1] == captured["CLAUDE_CONFIG_DIR"]


def test_native_mapping_actions_reuse_mapping_store_without_switching_credentials(monkeypatch, tmp_path):
    from claude_swap.mappings import MappingStore
    monkeypatch.setitem(sys.modules, "rumps", types.SimpleNamespace())
    client = MagicMock()
    with patch("claude_swap.shiftbar_mac.BackendClient", return_value=client):
        extension = shiftbar_mac.MenuExtension()
    folder = tmp_path / "folder with spaces"
    folder.mkdir()
    extension._choose_folder = lambda: str(folder)
    extension._refresh = MagicMock()
    switcher = types.SimpleNamespace(backup_dir=tmp_path / "store", resolve_account=MagicMock(return_value=("2", "synthetic@example.com", "org-1")))
    app = types.SimpleNamespace(switcher=switcher, refresh_async=MagicMock())
    extension._map(app, 2)
    assert MappingStore(switcher.backup_dir).get(folder)["email"] == "synthetic@example.com"
    extension._map(app, None)
    assert MappingStore(switcher.backup_dir).get(folder) is None
    client.request.assert_not_called()


def test_account_terminal_honors_folder_mapping_and_remote_control_setting(monkeypatch, tmp_path):
    from claude_swap.mappings import MappingStore
    monkeypatch.setitem(sys.modules, "rumps", types.SimpleNamespace())
    with patch("claude_swap.shiftbar_mac.BackendClient"):
        extension = shiftbar_mac.MenuExtension()
    extension.switcher = types.SimpleNamespace(backup_dir=tmp_path / "store")
    extension._choose_folder = lambda: str(tmp_path)
    extension.snapshot = {"config": {"autoProjectAccount": True, "preserveRemoteControl": False}}
    MappingStore(extension.switcher.backup_dir).set(tmp_path, "synthetic@example.com", "")
    with patch("claude_swap.shiftbar_mac.run_terminal") as opened:
        extension._claude_terminal(2)
    argv = opened.call_args.args[0]
    assert argv[-2:] == ["--engine", "run"]
    assert "--restore-rc" not in argv
    extension.snapshot = {"config": {"autoProjectAccount": False, "preserveRemoteControl": True}}
    with patch("claude_swap.shiftbar_mac.run_terminal") as opened:
        extension._claude_terminal(2)
    assert opened.call_args.args[0][-4:] == ["--engine", "run", "2", "--restore-rc"]




def _old_install(home: Path, *, agent: str | None = "com.patch-goblins.menubar") -> Path:
    old = home / "Library" / "Application Support" / "patch-goblins"
    (old / "auto-prime").mkdir(parents=True)
    (old / "settings.json").write_text('{"AutoPrime": true}')
    (old / "auto-prime" / "ledger.json").write_text("{}")
    if agent:
        (home / "Library" / "LaunchAgents").mkdir(parents=True)
        (home / "Library" / "LaunchAgents" / f"{agent}.plist").write_bytes(b"old")
    return old


def _clear_data_env(monkeypatch):
    for prefix in shiftbar_mac.ENV_PREFIXES:
        monkeypatch.delenv(prefix + "DATA", raising=False)


def test_migration_moves_patch_goblins_data_and_login_item(monkeypatch, tmp_path):
    import plistlib
    _clear_data_env(monkeypatch)
    old = _old_install(tmp_path)
    scripts = tmp_path / "Library" / "Application Support" / "Patch Goblins" / "terminal"
    scripts.mkdir(parents=True)
    (scripts / "session-1.command").write_text("#!/bin/bash")
    shiftbar_mac.migrate_from_patch_goblins(tmp_path, running=lambda _: False)
    new = tmp_path / "Library" / "Application Support" / "shiftbar"
    assert not old.exists()
    assert not scripts.parent.exists()  # the old throwaway Terminal scripts are gone
    assert (new / "settings.json").read_text() == '{"AutoPrime": true}'
    assert (new / "auto-prime" / "ledger.json").is_file()
    agents = tmp_path / "Library" / "LaunchAgents"
    assert not (agents / "com.patch-goblins.menubar.plist").exists()
    assert plistlib.loads((agents / "com.shiftbar.app.plist").read_bytes())["Label"] == "com.shiftbar.app"
    shiftbar_mac.migrate_from_patch_goblins(tmp_path, running=lambda _: pytest.fail("nothing left to migrate"))


def test_migration_merges_into_existing_folder_and_keeps_login_off(monkeypatch, tmp_path):
    _clear_data_env(monkeypatch)
    old = _old_install(tmp_path, agent=None)
    new = tmp_path / "Library" / "Application Support" / "shiftbar"
    new.mkdir(parents=True)
    (new / "settings.json").write_text("{}")
    shiftbar_mac.migrate_from_patch_goblins(tmp_path, running=lambda _: False)
    assert (new / "settings.json").read_text() == "{}"  # what Shiftbar already has wins
    assert (new / "auto-prime" / "ledger.json").is_file()
    assert (old / "settings.json").is_file()  # the clash stays behind, untouched
    assert not (tmp_path / "Library" / "LaunchAgents").exists()  # launch at login was off: it stays off


def test_migration_leaves_the_retired_agents_folder_where_git_knows_it(monkeypatch, tmp_path):
    _clear_data_env(monkeypatch)
    old = _old_install(tmp_path, agent=None)
    (old / "agents" / "repo-1").mkdir(parents=True)
    shiftbar_mac.migrate_from_patch_goblins(tmp_path, running=lambda _: False)
    new = tmp_path / "Library" / "Application Support" / "shiftbar"
    assert (new / "settings.json").is_file()
    assert not (new / "agents").exists()
    assert (old / "agents" / "repo-1").is_dir()
    # Only the agents folder left: nothing to carry over, the old app is not even asked about.
    shiftbar_mac.migrate_from_patch_goblins(tmp_path, running=lambda _: pytest.fail("nothing left to migrate"))


def test_migration_refuses_while_patch_goblins_runs(monkeypatch, tmp_path):
    _clear_data_env(monkeypatch)
    old = _old_install(tmp_path, agent="com.patchgoblins.app")
    with pytest.raises(ClaudeSwitchError, match="still running"):
        shiftbar_mac.migrate_from_patch_goblins(tmp_path, running=lambda path: path == old)
    assert (old / "settings.json").is_file()
    assert (tmp_path / "Library" / "LaunchAgents" / "com.patchgoblins.app.plist").exists()
    assert not (tmp_path / "Library" / "Application Support" / "shiftbar").exists()


def test_migration_skips_isolated_instances(monkeypatch, tmp_path):
    _clear_data_env(monkeypatch)
    monkeypatch.setenv("PATCH_GOBLINS_DATA", str(tmp_path / "isolated"))  # the pre-rename variable still isolates
    old = _old_install(tmp_path)
    shiftbar_mac.migrate_from_patch_goblins(tmp_path, running=lambda _: pytest.fail("isolated: never checked"))
    assert old.is_dir()
