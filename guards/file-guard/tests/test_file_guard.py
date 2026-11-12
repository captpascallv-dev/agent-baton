"""Offline checks for the public file guard. Targets stay inside a temp directory."""
from __future__ import annotations

import importlib
import json
import ntpath
import os
import shutil
import sys
import tempfile
import types
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import file_guard
import hook_adapter

_MISSING = object()


def _case_dir(name: str) -> Path:
    base = os.environ.get("FILE_GUARD_TEST_ROOT", "").strip()
    if base:
        path = Path(base) / name
        path.mkdir(parents=True, exist_ok=True)
    else:
        path = Path(tempfile.mkdtemp(prefix="file-guard-" + name + "-"))
    resolved = path.resolve()
    drive, tail = ntpath.splitdrive(str(resolved))
    parts = [part for part in tail.split("\\") if part]
    if not drive or len(parts) < 2:
        raise RuntimeError("refusing to use a disk root or a too-shallow test directory")
    return resolved


def _expected_default_roots() -> tuple[str, ...]:
    home = Path.home()
    guard = Path(file_guard.__file__).resolve().parent
    values = [
        home,
        home.parent,
        home / ".claude",
        home / ".codex",
        home / ".cursor",
        home / "Documents",
        home / "Projects",
        guard,
    ]
    output: list[str] = []
    seen: set[str] = set()
    for value in values:
        normalized = ntpath.normcase(ntpath.normpath(str(value)))
        if normalized not in seen:
            seen.add(normalized)
            output.append(normalized)
    return tuple(output)


@unittest.skipUnless(os.name == "nt", "Windows path-policy suite; see test_file_guard_mac.py")
class FileGuardTests(unittest.TestCase):
    def setUp(self) -> None:
        self._saved_env = {key: os.environ.get(key) for key in ("FILE_GUARD_CONFIG", "FILE_GUARD_PWSH")}
        os.environ.pop("FILE_GUARD_CONFIG", None)
        os.environ.pop("FILE_GUARD_PWSH", None)
        self._saved_send2trash = sys.modules.get("send2trash", _MISSING)
        self.root = _case_dir(self._testMethodName)
        self._trap_send2trash()

    def tearDown(self) -> None:
        if self._saved_send2trash is _MISSING:
            sys.modules.pop("send2trash", None)
        else:
            sys.modules["send2trash"] = self._saved_send2trash
        for key, value in self._saved_env.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value

    def _trap_send2trash(self) -> None:
        fake = types.ModuleType("send2trash")

        def send2trash(path: str) -> None:
            raise AssertionError("recycle was not supposed to run")

        fake.send2trash = send2trash
        sys.modules["send2trash"] = fake

    def _use_real_send2trash(self) -> None:
        sys.modules.pop("send2trash", None)
        module = importlib.import_module("send2trash")
        self.assertTrue(callable(module.send2trash))

    def write_config(self, payload: dict) -> None:
        path = self.root / "guard-config.json"
        path.write_text(json.dumps(payload), encoding="utf-8")
        os.environ["FILE_GUARD_CONFIG"] = str(path.resolve())

    def make_file(self, name: str, text: str = "keep") -> Path:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path.resolve()

    def test_rejects_variables_wildcards_and_relative_paths(self) -> None:
        cases = [
            (str(self.root / "item$name"), "DELETE_TARGET_DYNAMIC"),
            (str(self.root / "item*"), "DELETE_TARGET_DYNAMIC"),
            (str(self.root / "item%NAME%"), "DELETE_TARGET_DYNAMIC"),
            ("folder\\file.txt", "DELETE_TARGET_NOT_ABSOLUTE"),
            (str(self.root) + "\\a\\..\\b", "DELETE_TARGET_NOT_RESOLVED"),
        ]
        for target, code in cases:
            with self.subTest(target=target):
                output, status = file_guard.recycle([target])
                self.assertEqual(status, 2)
                self.assertEqual(output["code"], code)
                self.assertNotIn("results", output)
        commands = [
            ("del " + str(self.root / "item$name"), "DELETE_TARGET_DYNAMIC"),
            ("del " + str(self.root / "item*"), "DELETE_TARGET_DYNAMIC"),
            ("del folder\\file.txt", "DELETE_TARGET_NOT_ABSOLUTE"),
        ]
        for command, code in commands:
            with self.subTest(command=command):
                result = file_guard.inspect({"command": command, "cwd": str(self.root), "shell": "cmd"})
                self.assertEqual(result["decision"], "deny")
                self.assertEqual(result["code"], code)

    def test_parse_failure_denies(self) -> None:
        python_result = file_guard.inspect({"command": "os.remove(", "cwd": str(self.root), "shell": "python"})
        self.assertEqual(python_result["decision"], "deny")
        self.assertEqual(python_result["code"], "PYTHON_PARSE_ERROR")
        powershell_result = file_guard.inspect({"command": "Remove-Item '", "cwd": str(self.root), "shell": "powershell"})
        self.assertEqual(powershell_result["decision"], "deny")
        if file_guard.pwsh_executable():
            self.assertEqual(powershell_result["code"], "POWERSHELL_PARSE_ERROR")
        else:
            self.assertEqual(powershell_result["code"], "POWERSHELL_PARSER_UNAVAILABLE")
        variable = file_guard.inspect({"command": "Remove-Item $target", "cwd": str(self.root), "shell": "powershell"})
        self.assertEqual(variable["decision"], "deny")
        if file_guard.pwsh_executable():
            self.assertEqual(variable["code"], "DELETE_TARGET_UNKNOWN")

    def test_refuses_protected_root_ancestor_and_disk_root(self) -> None:
        vault = (self.root / "vault").resolve()
        vault.mkdir()
        inside = vault / "inside.txt"
        inside.write_text("keep", encoding="utf-8")
        self.write_config({"protected_roots": [str(vault)]})
        for target in (str(vault), str(vault.parent)):
            with self.subTest(target=target):
                output, status = file_guard.recycle([target])
                self.assertEqual(status, 2)
                self.assertEqual(output["code"], "PROTECTED_TARGET")
                self.assertTrue(inside.is_file())
                self.assertEqual(inside.read_text(encoding="utf-8"), "keep")
        drive, _tail = ntpath.splitdrive(str(self.root))
        for target in (drive + "\\", "\\"):
            output, status = file_guard.recycle([target])
            self.assertEqual(status, 2)
            self.assertEqual(output["code"], "PROTECTED_TARGET")
        failure, _canonical = file_guard.path_policy(str(inside))
        self.assertIsNone(failure)
        self.assertTrue(inside.is_file())

    def test_refuses_to_move_protected_root_or_ancestor(self) -> None:
        vault = (self.root / "vault").resolve()
        vault.mkdir()
        inside = vault / "inside.txt"
        inside.write_text("keep", encoding="utf-8")
        self.write_config({"protected_roots": [str(vault)]})
        destination = self.root / "elsewhere"
        for source in (vault, vault.parent):
            result = file_guard.inspect({
                "command": 'move "' + str(source) + '" "' + str(destination) + '"',
                "cwd": str(self.root),
                "shell": "cmd",
            })
            self.assertEqual(result["decision"], "deny")
            self.assertEqual(result["code"], "PROTECTED_MOVE_TARGET")
            self.assertTrue(inside.is_file())

    def test_batch_preflight_leaves_every_target(self) -> None:
        first = self.make_file("first.txt")
        second = self.make_file("second.txt")
        output, status = file_guard.recycle([str(first), str(second), "notes\\draft.txt"])
        self.assertEqual(status, 2)
        self.assertEqual(output["code"], "DELETE_TARGET_NOT_ABSOLUTE")
        self.assertNotIn("results", output)
        self.assertEqual(first.read_text(encoding="utf-8"), "keep")
        self.assertEqual(second.read_text(encoding="utf-8"), "keep")

    def test_visible_delete_is_not_executed(self) -> None:
        target = self.make_file("listed.txt")
        result = file_guard.inspect({"command": "del " + str(target), "cwd": str(self.root), "shell": "cmd"})
        self.assertEqual(result["code"], "USE_RECYCLER")
        self.assertTrue(target.is_file())

    def test_recycle_sends_one_file_to_trash(self) -> None:
        target = self.make_file("plain.txt")
        self.assertEqual(target.parent, self.root)
        self._use_real_send2trash()
        output, status = file_guard.recycle([str(target)])
        self.assertEqual(status, 0, output)
        self.assertEqual(output["code"], "RECYCLED")
        self.assertEqual(output["results"][0]["status"], "recycled")
        self.assertFalse(target.exists())

    def test_recycle_failure_keeps_the_file(self) -> None:
        target = self.make_file("stay.txt")
        fake = types.ModuleType("send2trash")

        def fail(path: str) -> None:
            raise OSError("simulated recycle failure")

        fake.send2trash = fail
        sys.modules["send2trash"] = fake
        output, status = file_guard.recycle([str(target)])
        self.assertEqual(status, 3)
        self.assertEqual(output["code"], "RECYCLE_FAILED")
        self.assertEqual(output["results"][0]["status"], "failed")
        self.assertEqual(target.read_text(encoding="utf-8"), "keep")

    def test_missing_recycler_keeps_the_file(self) -> None:
        target = self.make_file("stay.txt")
        sys.modules["send2trash"] = None
        output, status = file_guard.recycle([str(target)])
        self.assertEqual(status, 3)
        self.assertEqual(output["code"], "RECYCLER_UNAVAILABLE")
        self.assertEqual(target.read_text(encoding="utf-8"), "keep")

    def test_unreadable_config_denies_without_deleting(self) -> None:
        target = self.make_file("stay.txt")
        broken = self.root / "bad.json"
        broken.write_text("{", encoding="utf-8")
        os.environ["FILE_GUARD_CONFIG"] = str(broken.resolve())
        output, status = file_guard.recycle([str(target)])
        self.assertEqual(status, 2)
        self.assertEqual(output["code"], "CONFIG_INVALID")
        self.assertTrue(target.is_file())

    def test_defaults_when_unconfigured(self) -> None:
        local = Path(file_guard.__file__).resolve().with_name("file_guard.config.json")
        self.assertFalse(local.is_file())
        self.assertIsNone(file_guard.config_file())
        settings = file_guard.load_settings()
        self.assertEqual(settings["protected_roots"], _expected_default_roots())
        self.assertIsNone(settings["python"])
        self.assertIsNone(settings["pwsh"])
        self.assertIsNone(settings["session_rules_path"])
        guard = Path(file_guard.__file__).resolve().parent
        self.assertEqual(ntpath.normcase(settings["preimage_root"]), ntpath.normcase(str(guard / "preimages")))
        self.assertEqual(file_guard.python_runtime(), sys.executable)
        found = file_guard.pwsh_executable()
        expected = shutil.which("pwsh") or shutil.which("powershell")
        if expected:
            self.assertEqual(ntpath.normcase(found or ""), ntpath.normcase(expected))
        else:
            self.assertIsNone(found)
        example = guard / "file_guard.config.example.json"
        os.environ["FILE_GUARD_CONFIG"] = str(example)
        from_example = file_guard.load_settings()
        self.assertEqual(from_example["protected_roots"], _expected_default_roots())
        self.assertEqual(ntpath.normcase(from_example["preimage_root"]), ntpath.normcase(str(guard / "preimages")))
        self.assertEqual(file_guard.python_runtime(), sys.executable)

    def test_cursor_session_rules_are_opt_in(self) -> None:
        event = {"hook_event_name": "sessionStart"}
        self.assertIsNone(hook_adapter.adapt("cursor", event))
        missing = (self.root / "missing-rules.md").resolve()
        self.write_config({"session_rules_path": str(missing)})
        missing_output = hook_adapter.adapt("cursor", event)
        self.assertEqual(missing_output["additional_context"], "Before work, read the configured session rules.")
        rules = self.root / "rules.md"
        rules.write_text("hello rules", encoding="utf-8")
        self.write_config({"session_rules_path": str(rules.resolve())})
        short_output = hook_adapter.adapt("cursor", event)
        self.assertEqual(short_output["additional_context"], "hello rules")
        rules.write_text("x" * 9801, encoding="utf-8")
        long_output = hook_adapter.adapt("cursor", event)
        self.assertIn("Before work, read the complete configured session rules.", long_output["additional_context"])
        self.assertNotIn("x" * 40, long_output["additional_context"])


if __name__ == "__main__":
    unittest.main()
