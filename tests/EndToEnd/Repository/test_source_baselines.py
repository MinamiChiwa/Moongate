"""用隔离 Git 对象验证来源漂移、入口及许可登记。"""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location("source_baselines", ROOT / "scripts/maintenance/check-source-baselines.py")
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class SourceBaselineTests(unittest.TestCase):
    def setUp(self):
        self.data = json.loads(audit.MANIFEST.read_text(encoding="utf-8"))
        self.tasks = audit.load_tasks(ROOT)


    def test_registry_is_complete_and_mapped(self):
        self.assertEqual([], audit.validate(self.data, self.tasks))
        self.assertEqual({"mobifolio", "mabisync", "color-studio", "synccue", "mabiagent"},
                         {s["id"] for s in self.data["sources"]})
        self.assertGreaterEqual(len(self.data["features"]), 36)

    def test_author_owned_sources_are_registered_as_author_granted_mit(self):
        for sid in ("mabisync", "color-studio", "synccue"):
            source = next(s for s in self.data["sources"] if s["id"] == sid)
            self.assertEqual("author-granted-MIT", source["licenseStatus"])
            self.assertEqual(["MinamiChiwa（南千和）"], source["authors"])
            self.assertEqual("MIT", source["distributionLicense"]["id"])
            for field, invalid in (("authors", []), ("distributionLicense", {})):
                damaged = copy.deepcopy(self.data)
                entry = next(s for s in damaged["sources"] if s["id"] == sid)
                entry[field] = invalid
                self.assertTrue(audit.validate(damaged, self.tasks))

    def test_inventory_author_mit_grant_preserves_original_source_license(self):
        source = next(s for s in self.data["sources"] if s["id"] == "mabiagent")
        self.assertEqual("PolyForm-Noncommercial-1.0.0", source["licenseStatus"])
        original = next(f for f in source["files"] if f["path"] == "LICENSE")
        self.assertEqual("74bab30de8b346f584b2c7f3f6be2530aab01bb7137f5f90950d235c1deddafa", original["sha256"])
        grant = source["distributionLicense"]
        self.assertEqual("MIT", grant["id"])
        self.assertEqual("user", grant["authorizationSource"])
        self.assertEqual("Copyright (c) 2026 MinamiChiwa（南千和）", grant["copyright"])
        self.assertEqual("packaging/licenses/MinamiChiwa-MIT.txt", grant["path"])
        self.assertEqual(hashlib.sha256((ROOT / grant["path"]).read_bytes()).hexdigest(), grant["sha256"])
        for field, invalid in (("authorizationSource", ""), ("sha256", "0" * 64)):
            damaged = copy.deepcopy(self.data)
            entry = next(s for s in damaged["sources"] if s["id"] == "mabiagent")
            entry["distributionLicense"][field] = invalid
            self.assertTrue(audit.validate(damaged, self.tasks))

    def test_pr_provenance_rejects_missing_author_ambiguous_adoption_and_mutable_revision(self):
        source = next(s for s in self.data["sources"] if s["id"] == "mobifolio")
        path = source["files"][0]["path"]
        pull = {"number": 1, "baseCommit": "a" * 40, "patchBaseCommit": "a" * 40, "headCommit": "b" * 40,
                "commits": ["b" * 40], "authors": ["Contributor"], "includedInMain": False,
                "disposition": "adopted-function", "purpose": "采纳单个函数", "changedFiles": [path],
                "selectedFiles": [{"path": path, "sha256": "c" * 64, "functions": ["normalize"]}],
                "dependencies": ["Brotli==1.1.0"]}
        valid = copy.deepcopy(self.data)
        next(s for s in valid["sources"] if s["id"] == "mobifolio")["pullRequests"] = [pull]
        self.assertEqual([], audit.validate(valid, self.tasks))
        for change in ({"baseCommit": "main"}, {"authors": []}, {"includedInMain": True},
                       {"selectedFiles": []}, {"dependencies": ["Brotli>=1"]}):
            data = copy.deepcopy(valid)
            next(s for s in data["sources"] if s["id"] == "mobifolio")["pullRequests"][-1].update(change)
            self.assertTrue(audit.validate(data, self.tasks))

    def test_missing_evidence_entry_task_and_duplicate_are_rejected(self):
        for mutate in [
            lambda d: d["sources"][0].update(commit="main"),
            lambda d: d["sources"][0].update(licenseEvidence=[]),
            lambda d: d["features"][0].update(entries=["missing.cs"]),
            *([lambda d: d["features"][0].update(task="99.1")] if self.tasks is not None else []),
            lambda d: d["features"].append(copy.deepcopy(d["features"][0])),
            lambda d: d["sources"][0]["files"][0].update(path="../outside"),
            lambda d: d["features"][0].update(acceptance=" ")]:
            data = copy.deepcopy(self.data)
            mutate(data)
            self.assertTrue(audit.validate(data, self.tasks))

    def test_paths_reject_windows_and_posix_escapes(self):
        for path in ["../x", "/x", "C:/x", "a\\b", "./x", "a//b", "a/../b", "a\0b"]:
            self.assertFalse(audit.safe_path(path), path)
        self.assertTrue(audit.safe_path("src/繁體/Clock.cs"))

    def test_selected_pull_files_and_main_lineage_are_verified_from_git(self):
        (ROOT / "artifacts/tmp").mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/tmp") as directory:
            root = Path(directory)
            def git(*args):
                return subprocess.check_output(["git", "-C", directory, *args], stderr=subprocess.PIPE).decode().strip()
            def commit():
                git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                    "-c", "commit.gpgsign=false", "commit", "-qm", "fixture")
                return git("rev-parse", "HEAD")
            git("init", "-q")
            (root / "LICENSE").write_bytes(b"fixture\n")
            git("add", "LICENSE")
            baseline = commit()
            (root / "reference.py").write_bytes(b"result = 1\n")
            git("add", "reference.py")
            head = commit()
            pull = {"number": 1, "baseCommit": baseline, "patchBaseCommit": baseline,
                    "headCommit": head, "includedInMain": False,
                    "selectedFiles": [{"path": "reference.py", "sha256": hashlib.sha256(b"result = 1\n").hexdigest()}]}
            source = {"id": "fixture", "commit": baseline, "rootLicensePresent": True,
                      "files": [{"path": "LICENSE", "sha256": hashlib.sha256(b"fixture\n").hexdigest()}],
                      "pullRequests": [pull]}
            self.assertEqual([], audit.verify_source(source, root))
            pull["includedInMain"] = True
            self.assertTrue(any("祖先关系不符" in e for e in audit.verify_source(source, root)))
            pull["includedInMain"] = False
            pull["selectedFiles"][0]["sha256"] = "0" * 64
            self.assertTrue(any("采纳源码哈希不符" in e for e in audit.verify_source(source, root)))

    def test_exported_source_fixtures_match_provenance_hashes(self):
        manifest = json.loads((ROOT / "contracts/fixtures/source-fixtures.v1.json").read_text(encoding="utf-8"))
        self.assertEqual(1, manifest["schemaVersion"])
        for source in manifest["sources"]:
            self.assertRegex(source["commit"], r"^[0-9a-f]{40}$")
            for fixture in source["fixtures"]:
                path = ROOT / fixture["path"]
                content = path.read_bytes()
                if "sha256" in fixture:
                    self.assertEqual(fixture["sha256"], hashlib.sha256(content).hexdigest(), fixture["path"])
                if "sourceSha256" in fixture:
                    self.assertEqual(fixture["sourceSha256"], hashlib.sha256(content).hexdigest(), fixture["sourcePath"])
                if fixture["path"].endswith(".json"):
                    json.loads(content)
        room = json.loads((ROOT / "contracts/fixtures/room-protocol.v1.json").read_text(encoding="utf-8"))
        messages = [row["message"] for row in room["messages"]]
        self.assertEqual(1, room["protocolVersion"])
        self.assertEqual(["hello", "hello", "hello-accepted", "sync", "sync"],
                         [message["type"] for message in messages])
        self.assertFalse(any(key in message for message in messages for key in ("token", "password", "credential")))
        colors = json.loads((ROOT / "contracts/fixtures/color-rules.v1.json").read_text(encoding="utf-8"))
        self.assertEqual({"exact-with-alternatives", "similar-color-tolerance", "disabled-region"},
                         {case["name"] for case in colors["cases"]})

    def test_registered_working_tree_reads_local_bytes_and_rejects_drift_or_missing_snapshot(self):
        (ROOT / "artifacts/tmp").mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/tmp") as directory:
            root = Path(directory)
            def git(*args):
                return subprocess.check_output(["git", "-C", directory, *args], stderr=subprocess.PIPE).decode().strip()
            git("init", "-q")
            (root / "LICENSE").write_bytes(b"fixture license\n")
            (root / "reference.cs").write_bytes(b"committed baseline\n")
            git("add", "LICENSE", "reference.cs")
            git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "-qm", "fixture")
            (root / "reference.cs").write_bytes(b"local implementation\n")
            entries = [{"path": path, "sha256": hashlib.sha256((root / path).read_bytes()).hexdigest(),
                        "contentSource": "working-tree"} for path in ("LICENSE", "reference.cs")]
            snapshot = hashlib.sha256("".join(f"{entry['path']}\0{entry['sha256']}\n"
                for entry in sorted(entries, key=lambda entry: entry["path"])).encode()).hexdigest()
            source = {"id": "working-fixture", "repository": "Fixture/Project", "commit": git("rev-parse", "HEAD"),
                      "rootLicensePresent": True, "licenseStatus": "MIT", "licenseEvidence": ["LICENSE"],
                      "files": entries, "workingTree": {"sha256": snapshot, "capturedAt": "2026-11-10T00:00:00Z"}}
            data = {"schemaVersion": 1, "sources": [source], "features": [{"id": "working-feature",
                    "source": source["id"], "entries": ["reference.cs"], "task": "mabiagent-inventory-module:3.2",
                    "module": "inventory", "acceptance": "名称目录使用已登记工作树内容", "migrationStatus": "mapped"}]}
            self.assertEqual([], audit.validate(data, self.tasks))
            self.assertEqual([], audit.verify_source(source, root))
            changed = copy.deepcopy(data)
            changed["sources"][0]["workingTree"]["sha256"] = "0" * 64
            self.assertTrue(any("工作树快照登记无效" in error for error in audit.validate(changed, self.tasks)))
            missing = copy.deepcopy(source)
            del missing["workingTree"]
            self.assertTrue(any("工作树来源缺少快照" in error for error in audit.verify_source(missing, root)))
            committed = copy.deepcopy(source)
            committed["files"][1].pop("contentSource")
            self.assertTrue(any("固定源码哈希不符" in error for error in audit.verify_source(committed, root)))
            (root / "reference.cs").write_bytes(b"changed after capture\n")
            self.assertTrue(any("固定源码哈希不符" in error for error in audit.verify_source(source, root)))

    def test_fixed_git_objects_ignore_workspace_edits_and_detect_hash_drift(self):
        (ROOT / "artifacts/tmp").mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts/tmp") as directory:
            root = Path(directory)
            def git(*args):
                return subprocess.check_output(["git", "-C", directory, *args], stderr=subprocess.PIPE).decode().strip()
            git("init", "-q")
            (root / "LICENSE").write_bytes(b"fixture license\n")
            git("add", "LICENSE")
            git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "-qm", "fixture")
            import hashlib
            source = {"id": "fixture", "commit": git("rev-parse", "HEAD"), "rootLicensePresent": True,
                      "files": [{"path": "LICENSE", "sha256": hashlib.sha256(git("show", "HEAD:LICENSE").encode() + b"\n").hexdigest()}]}
            (root / "LICENSE").write_text("workspace edit", encoding="utf-8")
            self.assertEqual([], audit.verify_source(source, root))
            source["files"][0]["sha256"] = "0" * 64
            self.assertTrue(any("哈希不符" in e for e in audit.verify_source(source, root)))
            source["files"][0]["path"] = "missing"
            self.assertTrue(audit.verify_source(source, root))
            source["commit"] = "0" * 40
            self.assertTrue(audit.verify_source(source, root))


if __name__ == "__main__":
    unittest.main()
