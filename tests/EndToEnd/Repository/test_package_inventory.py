"""目录包清单：发行身份、组件集合、许可映射与解压版 zip 布局。"""
import json
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
import zlib
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
        icons = "frontends/desktop/src/MabiTool.Desktop/Assets/GameIcons/game-icons.lock.json"
        (self.root / icons).parent.mkdir(parents=True)
        shutil.copyfile(ROOT / icons, self.root / icons)
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
        # 桌面内嵌的游戏图标：登记文件随包保存，声明写明版权方并逐个列出哈希。
        icons = json.loads((ROOT / "frontends/desktop/src/MabiTool.Desktop/Assets/GameIcons/game-icons.lock.json").read_text(encoding="utf-8"))
        self.assertTrue((self.package / "components/core/licenses/game-icons.lock.json").is_file())
        entry = next(item for item in sbom["packages"] if item["name"] == "desktop-game-icons")
        self.assertTrue(entry["copyrightText"].startswith("Copyright Nexon / devCAT"))
        notices = (self.package / "THIRD_PARTY_NOTICES.txt").read_text(encoding="utf-8")
        for item in (icons["files"][0], next(item for item in icons["files"] if item["author"] == "自绘")):
            self.assertIn(f"{item['path']}  SHA-256 {item['sha256']}", notices)
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

    def game_cli(self, content=b"cli binary"):
        """被测仓库根的登记与包内组件：登记的长度与哈希取自 content，文件内容是测试用字节。"""
        lock = {"schemaVersion": 1, "id": "game-cli", "fileName": "MabinogiMobile_CLI.exe", "version": "0.1.0+test",
                "platform": "win-x64", "sha256": hashlib.sha256(content).hexdigest(), "length": len(content),
                "source": "测试来源。", "vendor": "Nexon / devCAT", "copyright": "版权归属 Nexon / devCAT。",
                "distribution": "由发行者随包分发，未修改该文件。"}
        (self.root / "packaging/game-cli").mkdir(parents=True, exist_ok=True)
        (self.root / "packaging/game-cli/game-cli.lock.json").write_text(json.dumps(lock, ensure_ascii=False), encoding="utf-8")
        component = self.package / "components/game-cli"
        component.mkdir()
        (component / lock["fileName"]).write_bytes(content)
        return component

    def test_game_cli_component_is_optional_registered_and_verified_against_the_lock(self):
        self.fixture()
        module = runpy.run_path(str(ROOT / "scripts/build/package-inventory.py"))
        self.assertEqual(0, self.inventory().returncode)
        identity = json.loads((self.package / "components/release.json").read_text(encoding="utf-8"))
        self.assertNotIn("game-cli", [item["id"] for item in identity["components"]])

        component = self.game_cli()
        result = self.inventory()
        self.assertEqual(0, result.returncode, result.stderr)
        identity = json.loads((self.package / "components/release.json").read_text(encoding="utf-8"))
        self.assertEqual(["core", "desktop", "dotnet-runtime", "game-cli", "room-server"], [item["id"] for item in identity["components"]])
        # 每个组件的树哈希都等于落盘后的目录内容：声明文件在 release.json 之前写入 core。
        for item in identity["components"]:
            self.assertEqual(module["tree_sha256"](self.package / "components" / item["id"]), (item["treeSha256"], item["files"]))
        self.assertEqual(1, next(item for item in identity["components"] if item["id"] == "game-cli")["files"])

        digest = hashlib.sha256(b"cli binary").hexdigest()
        sbom = json.loads((self.package / "sbom.spdx.json").read_text(encoding="utf-8"))
        entry = next(item for item in sbom["packages"] if item["name"] == "MabinogiMobile_CLI.exe")
        self.assertEqual(([dict(algorithm="SHA256", checksumValue=digest)], "Copyright Nexon / devCAT"), (entry["checksums"], entry["copyrightText"]))
        self.assertTrue(any(item["fileName"] == "./components/game-cli/MabinogiMobile_CLI.exe" for item in sbom["files"]))
        notices = (self.package / "THIRD_PARTY_NOTICES.txt").read_text(encoding="utf-8")
        for expected in (digest, "0.1.0+test", "Nexon / devCAT", "由发行者随包分发，未修改该文件"):
            self.assertIn(expected, notices)
        self.assertEqual(notices, (self.package / "components/core/THIRD_PARTY_NOTICES.txt").read_text(encoding="utf-8"))

        # 内容与登记不符（长度相同、哈希不同）或组件里有登记之外的文件时拒绝。
        executable = component / "MabinogiMobile_CLI.exe"
        executable.write_bytes(b"cli binarz")
        result = self.inventory()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("SHA-256", result.stderr)
        executable.write_bytes(b"cli binary")
        (component / "extra.txt").write_text("other")
        result = self.inventory()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("extra.txt", result.stderr)

    def life_vision(self, content=b"template pixels"):
        """被测仓库根的模板登记与包内组件：登记的长度与哈希取自 content。"""
        lock = {"schemaVersion": 1, "id": "life-vision-templates", "vendor": "Nexon / devCAT", "copyright": "版权归 Nexon / devCAT。",
                "source": "测试来源。", "distribution": "随包分发，按字节原样保存。",
                "files": [{"path": "templates/hook.png", "sha256": hashlib.sha256(content).hexdigest(), "length": len(content),
                           "origin": "test", "role": "上钩图标"}]}
        text = json.dumps(lock, ensure_ascii=False)
        (self.root / "workers/life-vision/Assets").mkdir(parents=True, exist_ok=True)
        (self.root / "workers/life-vision/Assets/templates.lock.json").write_text(text, encoding="utf-8")
        component = self.package / "components/life-vision"
        (component / "templates").mkdir(parents=True)
        (component / "templates/hook.png").write_bytes(content)
        (component / "templates/templates.lock.json").write_text(text, encoding="utf-8")
        (component / "MabiTool.LifeVision.Worker.dll").write_bytes(b"worker")
        return component

    def test_life_vision_component_ships_only_registered_templates_and_declares_their_vendor(self):
        self.fixture()
        component = self.life_vision()
        result = self.inventory()
        self.assertEqual(0, result.returncode, result.stderr)
        identity = json.loads((self.package / "components/release.json").read_text(encoding="utf-8"))
        self.assertIn("life-vision", [item["id"] for item in identity["components"]])
        sbom = json.loads((self.package / "sbom.spdx.json").read_text(encoding="utf-8"))
        entry = next(item for item in sbom["packages"] if item["name"] == "life-vision-templates")
        self.assertEqual("Copyright Nexon / devCAT", entry["copyrightText"])
        notices = (self.package / "THIRD_PARTY_NOTICES.txt").read_text(encoding="utf-8")
        for expected in (hashlib.sha256(b"template pixels").hexdigest(), "Nexon / devCAT", "templates/hook.png"):
            self.assertIn(expected, notices)

        (component / "templates/hook.png").write_bytes(b"template pixelz")
        result = self.inventory()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("SHA-256", result.stderr)
        (component / "templates/hook.png").write_bytes(b"template pixels")
        (component / "templates/extra.png").write_bytes(b"stray")
        result = self.inventory()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("extra.png", result.stderr)

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


class PublishCliInputTests(unittest.TestCase):
    """publish-backend.ps1 在创建工作区之前核对命令行工具输入（-CliExecutable 或仓库内的文件）：任何不符都在发布步骤之前失败，不留下发布工作区。"""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(dir=ROOT / "artifacts/tmp")
        self.root = Path(self.directory.name)
        self.lock = json.loads((ROOT / "packaging/game-cli/game-cli.lock.json").read_text(encoding="utf-8"))

    def tearDown(self):
        self.directory.cleanup()

    def publish(self, executable):
        script = ROOT / "scripts/build/publish-backend.ps1"
        command = ("[Console]::OutputEncoding = [Text.UTF8Encoding]::new(); "
                   f"try {{ & '{script}' -CliExecutable '{executable}' }} catch {{ [Console]::Error.WriteLine($_.Exception.Message); exit 1 }}")
        before = {path.name for path in (ROOT / "artifacts/tmp").glob("publish-*")}
        result = subprocess.run([shutil.which("pwsh") or "powershell", "-NoProfile", "-Command", command],
                                capture_output=True, text=True, encoding="utf-8", errors="replace")
        self.assertEqual(before, {path.name for path in (ROOT / "artifacts/tmp").glob("publish-*")})
        self.assertNotEqual(0, result.returncode)
        return result.stderr

    def test_repository_cli_matches_its_registration(self):
        data = (ROOT / "packaging/game-cli" / self.lock["fileName"]).read_bytes()
        self.assertEqual(self.lock["length"], len(data))
        self.assertEqual(self.lock["sha256"], hashlib.sha256(data).hexdigest())

    def test_cli_input_is_rejected_before_any_publish_step(self):
        length = self.lock["length"]
        self.assertIn("不存在", self.publish(self.root / "missing" / self.lock["fileName"]))
        other = self.root / "tool.exe"
        other.write_bytes(bytes(length))
        self.assertIn("文件名须为 " + self.lock["fileName"], self.publish(other))
        folder = self.root / "input"
        folder.mkdir()
        executable = folder / self.lock["fileName"].lower()
        executable.write_bytes(bytes(length - 1))
        self.assertIn(f"长度 {length - 1} ", self.publish(executable))
        executable.write_bytes(bytes(length))
        self.assertIn("SHA-256", self.publish(executable))
        if os.name == "nt":
            junction = self.root / "link"
            result = subprocess.run(["cmd", "/c", "mklink", "/J", str(junction), str(folder)], capture_output=True)
            self.assertEqual(0, result.returncode)
            try:
                self.assertIn("链接", self.publish(junction / executable.name))
            finally:
                os.rmdir(junction)


class UpdateAssetsTests(unittest.TestCase):
    """更新清单：schema 2 为每个文件记录完整包里的数据区，按数据区切出的内容与文件一致；给 --base 时仍写 schema 1 与旧式增量包。"""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(dir=ROOT / "artifacts/tmp")
        self.root = Path(self.directory.name)

    def tearDown(self):
        self.directory.cleanup()

    def package(self, version, files):
        archive = self.root / f"Moongate-v{version}-Windows-x64.zip"
        with zipfile.ZipFile(archive, "w") as content:
            content.writestr("Moongate/components/", b"")
            content.writestr("Moongate/components/release.json", json.dumps({"displayVersion": version}), zipfile.ZIP_DEFLATED)
            for name, (data, method) in files.items():
                content.writestr("Moongate/" + name, data, method)
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        (self.root / (archive.name + ".sha256")).write_text(f"{digest}  {archive.name}\n", encoding="ascii")
        return archive

    def assets(self, archive, *bases):
        arguments = [sys.executable, "-B", "-X", "utf8", str(ROOT / "scripts/build/package-update-assets.py"), "--archive", str(archive)]
        for base in bases:
            arguments += ["--base", str(base)]
        return subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8")

    def test_manifest_locates_every_file_inside_the_archive(self):
        files = {"Moongate.exe": (b"launcher" * 50, zipfile.ZIP_STORED),
                 "components/core/a.dll": (bytes(range(256)) * 400, zipfile.ZIP_DEFLATED),
                 "components/core/empty.txt": (b"", zipfile.ZIP_DEFLATED),
                 "licenses/NOTICE.txt": (b"notice", zipfile.ZIP_STORED)}
        archive = self.package("0.1.5", files)
        result = self.assets(archive)
        self.assertEqual(0, result.returncode, result.stderr)
        manifest = json.loads((self.root / (archive.name + ".manifest.json")).read_text(encoding="utf-8"))
        self.assertEqual((2, []), (manifest["schemaVersion"], manifest["deltas"]))
        raw = archive.read_bytes()
        for entry in manifest["files"]:
            chunk = raw[entry["dataOffset"]:entry["dataOffset"] + entry["compressedSize"]]
            if entry["compression"] == "deflate":
                inflater = zlib.decompressobj(-15)
                chunk = inflater.decompress(chunk) + inflater.flush()
            self.assertEqual((entry["size"], entry["sha256"], entry["crc32"]),
                             (len(chunk), hashlib.sha256(chunk).hexdigest(), zlib.crc32(chunk)), entry["path"])
        self.assertEqual({"stored", "deflate"}, {entry["compression"] for entry in manifest["files"]})

        # 附旧式增量包时清单保持 schema 1，不带数据区，0.1.2／0.1.3 可以读取。
        newer = self.package("0.1.6", files | {"components/core/a.dll": (b"changed" * 900, zipfile.ZIP_DEFLATED)})
        result = self.assets(newer, archive.with_name(archive.name + ".manifest.json"))
        self.assertEqual(0, result.returncode, result.stderr)
        legacy = json.loads((self.root / (newer.name + ".manifest.json")).read_text(encoding="utf-8"))
        self.assertEqual(1, legacy["schemaVersion"])
        self.assertEqual(["0.1.5"], [delta["fromVersion"] for delta in legacy["deltas"]])
        self.assertTrue(all(set(entry) == {"path", "size", "sha256"} for entry in legacy["files"]))


if __name__ == "__main__":
    unittest.main()
