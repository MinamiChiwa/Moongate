"""目录包清单：发行身份、组件集合、许可映射与解压版 zip 布局。"""
import json
import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
import runpy

ROOT = Path(__file__).resolve().parents[3]


class PackageInventoryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(dir=ROOT / "artifacts/tmp")
        self.root = Path(self.directory.name)
        self.package = self.root / "artifacts/package"
        self.package.mkdir(parents=True)

    def tearDown(self):
        self.directory.cleanup()

    def inventory(self, version="0.4.7", extra=()):
        arguments = [sys.executable, "-B", "-X", "utf8", str(ROOT / "scripts/build/package-inventory.py"),
                     "--repository", str(self.root), "--package", str(self.package),
                     "--version", version, "--runtime-version", "10.0.13", "--release-id", "test-release",
                     "--channel", "preview", "--nuget-packages", str(self.root / "nuget")]
        arguments += list(extra)
        return subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8")

    def fixture(self):
        for component in ("core", "room-server", "dotnet-runtime", "desktop"):
            (self.package / "components" / component).mkdir(parents=True)
        (self.package / "Moongate.exe").write_bytes(b"portable launcher")
        fonts = "frontends/desktop/src/MabiTool.Desktop/Assets/Fonts/OFL.txt"
        (self.root / fonts).parent.mkdir(parents=True)
        shutil.copyfile(ROOT / fonts, self.root / fonts)
        (self.root / "packaging/licenses").mkdir(parents=True)
        shutil.copyfile(ROOT / "packaging/licenses/MinamiChiwa-MIT.txt", self.root / "packaging/licenses/MinamiChiwa-MIT.txt")
        shutil.copyfile(ROOT / "LICENSE", self.root / "LICENSE")
        (self.root / "scripts/maintenance").mkdir(parents=True)
        (self.root / "scripts/maintenance/source-baselines.json").write_text(json.dumps({"sources": [], "features": []}))
        for name in ("LICENSE.txt", "ThirdPartyNotices.txt"):
            (self.package / "components/dotnet-runtime" / name).write_text("runtime notice")
        (self.package / "components/core/MabiTool.Host.dll").write_bytes(b"host")
        (self.package / "components/desktop/sub").mkdir()
        (self.package / "components/desktop/sub/view.dll").write_bytes(b"view")

    def test_inventory_writes_release_identity_with_component_tree_hashes(self):
        self.fixture()
        result = self.inventory()
        self.assertEqual(0, result.returncode, result.stderr)
        identity = json.loads((self.package / "components/release.json").read_text(encoding="utf-8"))
        self.assertEqual(("Moongate", "0.4.7", "preview", "0.4.7-preview", "MinamiChiwa/Moongate"),
                         (identity["product"], identity["version"], identity["channel"], identity["displayVersion"], identity["repository"]))
        self.assertEqual(hashlib.sha256(b"portable launcher").hexdigest(), identity["entry"]["sha256"])
        self.assertEqual(["core", "desktop", "dotnet-runtime", "room-server"], [item["id"] for item in identity["components"]])
        module = runpy.run_path(str(ROOT / "scripts/build/package-inventory.py"))
        for item in identity["components"]:
            self.assertEqual(module["tree_sha256"](self.package / "components" / item["id"]), (item["treeSha256"], item["files"]))
        sbom = json.loads((self.package / "sbom.spdx.json").read_text(encoding="utf-8"))
        self.assertTrue(any(item["fileName"] == "./components/release.json" for item in sbom["files"]))
        self.assertFalse((self.package / "release-input.json").exists())
        # 树哈希的规则与 Moongate.exe、宿主的 C# 实现共用同一组向量（ReleaseIdentity.TreeSha256）。
        vector = self.root / "vector"
        (vector / "b").mkdir(parents=True)
        (vector / "a.txt").write_bytes(b"alpha")
        (vector / "B.txt").write_bytes(b"beta")
        (vector / "b/c.txt").write_bytes(b"gamma")
        self.assertEqual(("0c4cc6beb8fd7294852602c7d566e726e38a9eed3f4a8c8da0f674cf5b14d4f5", 3), module["tree_sha256"](vector))
        # 发行组件集合不再包含 updater 与 launcher。
        (self.package / "components/updater").mkdir()
        result = self.inventory()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("组件闭包", result.stderr)

    def test_inventory_applies_registered_license_override_only_with_matching_evidence(self):
        self.fixture()
        (self.package / "components/desktop/MabiTool.Desktop.deps.json").write_text(json.dumps({
            "libraries": {"Avalonia.Angle.Windows.Natives/2.1.0": {"type": "package"}}}))
        location = self.root / "nuget/avalonia.angle.windows.natives/2.1.0"
        location.mkdir(parents=True)
        (location / "avalonia.angle.windows.natives.nuspec").write_text(
            '<package><metadata><id>Avalonia.Angle.Windows.Natives</id><license type="file">LICENSE</license></metadata></package>')
        (location / "LICENSE").write_text("ANGLE three-clause BSD text")
        override = {"id": "Avalonia.Angle.Windows.Natives", "license": "BSD-3-Clause", "licenseFile": "LICENSE",
                    "licenseSha256": hashlib.sha256(b"ANGLE three-clause BSD text").hexdigest(), "evidence": "upstream ANGLE"}
        (self.root / "packaging/licenses/nuget-license-overrides.json").write_text(json.dumps({"schemaVersion": 1, "packages": [override]}))
        result = self.inventory()
        self.assertEqual(0, result.returncode, result.stderr)
        sbom = json.loads((self.package / "sbom.spdx.json").read_text(encoding="utf-8"))
        angle = next(item for item in sbom["packages"] if item["name"] == "Avalonia.Angle.Windows.Natives")
        self.assertEqual(("BSD-3-Clause", "BSD-3-Clause", "upstream ANGLE"),
                         (angle["licenseDeclared"], angle["licenseConcluded"], angle["comment"]))
        notices = (self.package / "THIRD_PARTY_NOTICES.txt").read_text(encoding="utf-8")
        self.assertIn("Avalonia.Angle.Windows.Natives 2.1.0\nNOASSERTION\nLicense: BSD-3-Clause\nLicense basis: upstream ANGLE\nANGLE three-clause BSD text", notices)
        (location / "LICENSE").write_text("changed text")
        result = self.inventory()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("许可映射的依据文件与登记不符", result.stderr)

    def test_inventory_requires_launcher_and_clean_package_root(self):
        self.fixture()
        launcher = self.package / "Moongate.exe"
        launcher.unlink()
        result = self.inventory()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Moongate.exe", result.stderr)
        launcher.write_bytes(b"portable launcher")
        (self.package / "launch-backend.cmd").write_text("old root entry")
        result = self.inventory()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("launch-backend.cmd", result.stderr)
        (self.package / "launch-backend.cmd").unlink()
        result = self.inventory()
        self.assertEqual(0, result.returncode, result.stderr)
        sbom = json.loads((self.package / "sbom.spdx.json").read_text(encoding="utf-8"))
        entry = next(item for item in sbom["files"] if item["fileName"] == "./Moongate.exe")
        self.assertEqual(hashlib.sha256(b"portable launcher").hexdigest(), entry["checksums"][0]["checksumValue"])

    def test_inventory_rejects_debug_symbols(self):
        self.fixture()
        (self.package / "components/core/MabiTool.Host.pdb").write_bytes(b"symbols")
        result = self.inventory("0.4.6")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("components/core/MabiTool.Host.pdb", result.stderr)
        self.assertFalse((self.package / "sbom.spdx.json").exists())

    def archive(self, archive):
        script = ROOT / "scripts/build/package-archive.ps1"
        return subprocess.run([shutil.which("pwsh") or "powershell", "-NoProfile", "-File", str(script),
                               "-PackageDirectory", str(self.package), "-ArchivePath", str(archive)],
                              capture_output=True, text=True, encoding="utf-8", errors="replace")

    def test_archive_is_single_folder_with_launcher_components_and_licenses(self):
        self.fixture()
        self.assertEqual(0, self.inventory().returncode)
        (self.package / "package-build.json").write_text("{}")
        archive = self.root / "Moongate-v0.4.7-preview-Windows-x64.zip"
        result = self.archive(archive)
        self.assertEqual(0, result.returncode, result.stderr)
        with zipfile.ZipFile(archive) as content:
            names = content.namelist()
            self.assertEqual({"Moongate"}, {name.split("/")[0] for name in names})
            self.assertEqual({"Moongate.exe", "components", "licenses"}, {name.split("/")[1] for name in names})
            self.assertEqual(b"portable launcher", content.read("Moongate/Moongate.exe"))
            self.assertIn("Moongate/licenses/THIRD_PARTY_NOTICES.txt", names)
            self.assertIn("Moongate/licenses/sbom.spdx.json", names)
            self.assertIn("Moongate/components/release.json", names)
            self.assertFalse(any(name.endswith("package-build.json") for name in names))
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        self.assertEqual(f"{digest}  {archive.name}\n", (self.root / (archive.name + ".sha256")).read_text(encoding="ascii"))
        self.assertNotEqual(0, self.archive(archive).returncode)

        # 包根出现布局之外的文件时拒绝打包，不留下半成品。
        (self.package / "launch-backend.cmd").write_text("old root entry")
        rejected = self.root / "rejected.zip"
        result = self.archive(rejected)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("launch-backend.cmd", result.stderr)
        self.assertEqual([], list(self.root.glob("rejected.zip*")))



if __name__ == "__main__":
    unittest.main()
