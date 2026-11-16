"""最低更新器发行参数与实际元数据生成验证。"""
import json
import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import runpy

ROOT = Path(__file__).resolve().parents[3]


class MinimumUpdaterPublicationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(dir=ROOT / "artifacts/tmp")
        self.root = Path(self.directory.name)
        self.package = self.root / "artifacts/package"
        self.package.mkdir(parents=True)

    def tearDown(self):
        self.directory.cleanup()

    def inventory(self, minimum=None, version="0.4.7", extra=()):
        arguments = [sys.executable, "-B", "-X", "utf8", str(ROOT / "scripts/build/package-inventory.py"),
                     "--repository", str(self.root), "--package", str(self.package),
                     "--version", version, "--runtime-version", "10.0.13", "--release-id", "test-release",
                     "--release-sequence", "11", "--channel", "preview", "--nuget-packages", str(self.root / "nuget")]
        if minimum is not None:
            arguments += ["--minimum-updater-version=" + minimum]
        arguments += list(extra)
        return subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8")

    def fixture(self):
        for component in ("core", "updater", "launcher", "room-server", "dotnet-runtime", "desktop"):
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
        (self.package / "components/core/install-target.json").write_text(json.dumps({
            "hostVersion": "0.4.7", "runtimeMode": "framework-dependent",
            "migrations": [{"moduleId": "core", "version": 1}]}))
        for name in ("LICENSE.txt", "ThirdPartyNotices.txt"):
            (self.package / "components/dotnet-runtime" / name).write_text("runtime notice")
        (self.package / "components/launcher/MabiTool.Launcher.deps.json").write_text(json.dumps({
            "libraries": {"MabiTool.Launcher/0.4.7": {"type": "project"}}}))
        (self.package / "components/launcher/MabiTool.Launcher.dll").write_bytes(b"launcher")
        updater = self.package / "components/updater"
        (updater / "MabiTool.Updater.dll").write_bytes(b"updater dll")
        (updater / "MabiTool.Updater.exe").write_bytes(b"updater apphost")
        pack = self.root / "nuget/microsoft.netcore.app.runtime.win-x64/10.0.13"
        (updater / "MabiTool.Updater.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
            "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.13"}]}}))
        (updater / "MabiTool.Updater.deps.json").write_text(json.dumps({
            "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/win-x64"},
            "libraries": {"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.13": {"type": "runtimepack"}}}))
        for name, directory in (("hostpolicy.dll", "native"), ("coreclr.dll", "native"),
                                ("System.Private.CoreLib.dll", "lib/net10.0"), ("System.Runtime.dll", "lib/net10.0")):
            path = pack / "runtimes/win-x64" / directory / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(name)
            (updater / name).write_text(name)
        for name in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
            (pack / name).write_text("runtime license " + name)
            (updater / name).write_text("runtime license " + name)

    def test_inventory_default_and_explicit_minimum_generate_metadata(self):
        self.fixture()
        for minimum, expected in ((None, "0.1.0"), ("0.4.6", "0.4.6"), ("0.4.7", "0.4.7"), ("0.0.0", "0.0.0")):
            with self.subTest(minimum=minimum):
                result = self.inventory(minimum)
                self.assertEqual(0, result.returncode, result.stderr)
                metadata = json.loads((self.package / "release-input.json").read_text(encoding="utf-8"))
                self.assertEqual("mabitool", metadata["productId"])
                self.assertEqual(expected, metadata["minUpdaterVersion"])
                updater = next(item for item in metadata["components"] if item["componentId"] == "updater")
                self.assertEqual("0.4.7", updater["version"])
                self.assertEqual([], updater["dependencies"])
                self.assertEqual("framework-dependent", metadata["runtimeMode"])
                sbom = json.loads((self.package / "sbom.spdx.json").read_text(encoding="utf-8"))
                self.assertTrue(any(item["name"] == "Moongate" for item in sbom["packages"]))
                notices = (self.package / "THIRD_PARTY_NOTICES.txt").read_text(encoding="utf-8")
                self.assertIn("Moongate 发行来源", notices)
                self.assertNotIn("MabiTool 发行来源", notices)
                runtime = next(item for item in sbom["packages"] if item["name"] == "Microsoft.NETCore.App.Runtime.win-x64")
                self.assertEqual("10.0.13", runtime["versionInfo"])
                self.assertTrue(any(item["licenseId"] == "LicenseRef-Dotnet-Updater-Distribution" for item in sbom["hasExtractedLicensingInfos"]))
                for name in ("MabiTool.Updater.dll", "MabiTool.Updater.exe"):
                    self.assertTrue(any(item["fileName"] == "./components/updater/" + name for item in sbom["files"]))
                sources = json.loads((self.package / "components/core/source-inventory.json").read_text(encoding="utf-8"))
                distribution = next(item for item in sources["runtimeDistributions"] if item["componentId"] == "updater")
                self.assertEqual("self-contained", distribution["runtimeMode"])
                self.assertEqual("10.0.13", distribution["version"])

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

    def test_inventory_rejects_invalid_or_newer_minimum_before_writes(self):
        for minimum in ("0.4", "0.4.6.0", "-1.0.0", "0.4.6-preview", "0.4.8", "0.10.0", "999999999999999999999.0.0", "", "0.4.6\n"):
            with self.subTest(minimum=minimum):
                result = self.inventory(minimum)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("更新器版本", result.stderr)
                self.assertEqual([], list(self.package.iterdir()))

    def powershell(self, minimum=None, version="0.4.7", stable_source=None):
        # 执行原脚本的参数块和构建前校验，再执行实际清单命令及构建记录表达式。
        harness = self.root / "parameters.ps1"
        harness.write_text(r'''
param([string]$Repository, [string]$Output, [string]$TestVersion, [string]$TestMinimum, [switch]$UseDefault, [string]$TestStableSource, [string]$TestStableRecord)
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $Repository 'scripts/build/publish-backend.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$statements = @($ast.EndBlock.Statements)
$preamble = @()
foreach ($statement in $statements) {
    if ($statement -is [Management.Automation.Language.AssignmentStatementAst] -and $statement.Left.Extent.Text -eq '$workRoot') { break }
    $preamble += $statement.Extent.Text
}
$body = $ast.ParamBlock.Extent.Text + "`n" + '$PSScriptRoot = ' + "'" + (Join-Path $Repository 'scripts/build').Replace("'", "''") + "'`n" + ($preamble -join "`n")
$body += @'

$workRoot = $Output
$PythonExecutable = 'CaptureArguments'
function CaptureArguments { $script:forwarded = $args }
$startedAt = [DateTimeOffset]::UtcNow
$dotnetCommand = 'FakeDotnet'
function FakeDotnet { '10.0.100' }
$projects = [ordered]@{ core = 'core'; updater = 'updater' }
'@
$inventoryCommand = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.Extent.Text.Contains('scripts/build/package-inventory.py') }, $true)[-1]
$record = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$buildRecord' }, $true)[0]
$body += "`n" + $inventoryCommand.Extent.Text + "`n" + $record.Extent.Text
$body += "`n" + '$buildRecord | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Output ''package-build.json'')' + "`n" + '$script:forwarded | ConvertTo-Json | Set-Content (Join-Path $Output ''forwarded.json'')'
$arguments = @{ Version = $TestVersion; OutputDirectory = $Output }
if (-not $UseDefault) { $arguments.MinimumUpdaterVersion = $TestMinimum }
if ($TestStableSource) { $arguments.StableLauncherPackage = $TestStableSource }
& ([scriptblock]::Create($body)) @arguments
''', encoding="utf-8")
        output = ROOT / "artifacts/packages" / ("parameter-test-" + self.root.name)
        # 前置校验要求输出尚不存在；只有校验成功后测试替身创建记录目录。
        text = harness.read_text()
        text = text.replace("function CaptureArguments { $script:forwarded = $args }", "function CaptureArguments { New-Item -ItemType Directory -Path $Output | Out-Null; if ($TestStableRecord) { New-Item -ItemType Directory -Path (Join-Path $Output 'components/core') | Out-Null; Copy-Item $TestStableRecord (Join-Path $Output 'components/core/stable-launcher-source.json') }; $script:forwarded = $args }")
        harness.write_text(text, encoding="utf-8")
        command = [shutil.which("pwsh") or "powershell", "-NoProfile", "-File", str(harness),
                   "-Repository", str(ROOT), "-Output", str(output), "-TestVersion", version]
        command += ["-UseDefault"] if minimum is None else ["-TestMinimum", minimum]
        if stable_source is not None:
            command += ["-TestStableSource", str(stable_source), "-TestStableRecord", str(self.package / "components/core/stable-launcher-source.json")]
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
        try:
            record = json.loads((output / "package-build.json").read_text(encoding="utf-8-sig")) if output.exists() else None
            forwarded = json.loads((output / "forwarded.json").read_text(encoding="utf-8-sig")) if output.exists() else None
            return result, record, forwarded
        finally:
            if output.exists():
                shutil.rmtree(output)

    def test_publish_accepts_parameter_default_and_forwards_and_records_same_value(self):
        for minimum, expected in ((None, "0.1.0"), ("0.4.6", "0.4.6"), ("0.4.7", "0.4.7")):
            with self.subTest(minimum=minimum):
                result, record, forwarded = self.powershell(minimum)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(expected, record["minUpdaterVersion"])
                self.assertTrue(record["updaterPublication"]["selfContained"])
                self.assertTrue(record["updaterPublication"]["useAppHost"])
                self.assertEqual(expected, forwarded[forwarded.index("--minimum-updater-version") + 1])

    def test_publish_rejects_invalid_or_newer_minimum_before_output(self):
        for minimum in ("0.4", "0.4.6.0", "-1.0.0", "0.4.6-preview", "0.4.8", "0.10.0", "999999999999999999999.0.0"):
            with self.subTest(minimum=minimum):
                result, record, forwarded = self.powershell(minimum)
                self.assertNotEqual(0, result.returncode)
                self.assertIsNone(record)
                self.assertIsNone(forwarded)

    def test_inventory_rejects_mismatched_runtime_or_missing_original_content(self):
        self.fixture()
        updater = self.package / "components/updater"
        for filename in ("MabiTool.Updater.dll", "MabiTool.Updater.exe", "MabiTool.Updater.runtimeconfig.json", "MabiTool.Updater.deps.json", "hostpolicy.dll",
                         "coreclr.dll", "System.Private.CoreLib.dll", "System.Runtime.dll", "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
            with self.subTest(file=filename):
                path = updater / filename
                original = path.read_bytes()
                if filename in ("MabiTool.Updater.dll", "MabiTool.Updater.exe"):
                    path.unlink()
                elif filename.endswith(".json"):
                    path.write_bytes(original.replace(b'10.0.13', b'10.0.12'))
                else:
                    path.write_text("changed")
                try:
                    result = self.inventory("0.4.6")
                    self.assertNotEqual(0, result.returncode)
                    self.assertFalse((self.package / "release-input.json").exists())
                    self.assertFalse((updater / "licenses").exists())
                finally:
                    path.write_bytes(original)

    def test_inventory_requires_launcher_and_clean_package_root(self):
        self.fixture()
        launcher = self.package / "Moongate.exe"
        launcher.unlink()
        result = self.inventory("0.4.6")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Moongate.exe", result.stderr)
        launcher.write_bytes(b"portable launcher")
        (self.package / "launch-backend.cmd").write_text("old root entry")
        result = self.inventory("0.4.6")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("launch-backend.cmd", result.stderr)
        (self.package / "launch-backend.cmd").unlink()
        result = self.inventory("0.4.6")
        self.assertEqual(0, result.returncode, result.stderr)
        sbom = json.loads((self.package / "sbom.spdx.json").read_text(encoding="utf-8"))
        entry = next(item for item in sbom["files"] if item["fileName"] == "./Moongate.exe")
        self.assertEqual(hashlib.sha256(b"portable launcher").hexdigest(), entry["checksums"][0]["checksumValue"])

    def archive(self, archive):
        script = ROOT / "scripts/build/package-archive.ps1"
        return subprocess.run([shutil.which("pwsh") or "powershell", "-NoProfile", "-File", str(script),
                               "-PackageDirectory", str(self.package), "-ArchivePath", str(archive)],
                              capture_output=True, text=True, encoding="utf-8", errors="replace")

    def test_archive_is_single_folder_with_launcher_components_and_licenses(self):
        self.fixture()
        self.assertEqual(0, self.inventory("0.4.6").returncode)
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
            self.assertIn("Moongate/components/core/install-target.json", names)
            self.assertFalse(any(name.endswith(("release-input.json", "package-build.json")) for name in names))
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

    def stable_fixture(self):
        source = self.root / "stable-source"
        launcher = source / "components/launcher"
        launcher.mkdir(parents=True)
        (launcher / "MabiTool.Launcher.dll").write_bytes(b"frozen launcher")
        (launcher / "MabiTool.Launcher.deps.json").write_text(json.dumps({"libraries": {"MabiTool.Launcher/0.4.4": {"type": "project"}}}))
        (source / "release-input.json").write_text(json.dumps({"releaseId": "frozen-0.4.4", "releaseSequence": 8,
            "components": [{"componentId": "launcher", "directory": "components/launcher", "version": "0.4.4"}]}))
        files = [{"fileName": "./components/launcher/" + path.name,
                  "checksums": [{"algorithm": "SHA256", "checksumValue": hashlib.sha256(path.read_bytes()).hexdigest()}]}
                 for path in launcher.iterdir()]
        (source / "sbom.spdx.json").write_text(json.dumps({"files": files}))
        return source

    def test_stable_launcher_copy_preserves_tree_actual_version_and_runtime_dependency(self):
        self.fixture()
        source = self.stable_fixture()
        original = {path.name: path.read_bytes() for path in (source / "components/launcher").iterdir()}
        shutil.rmtree(self.package / "components/launcher")
        result = self.inventory("0.4.6", extra=("--stable-launcher-package", str(source), "--copy-stable-launcher"))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(original, {path.name: path.read_bytes() for path in (self.package / "components/launcher").iterdir()})
        result = self.inventory("0.4.6")
        self.assertEqual(0, result.returncode, result.stderr)
        metadata = json.loads((self.package / "release-input.json").read_text(encoding="utf-8"))
        launcher = next(item for item in metadata["components"] if item["componentId"] == "launcher")
        self.assertEqual("0.4.4", launcher["version"])
        self.assertEqual([{"componentId": "dotnet-runtime", "minimumVersion": "10.0.13"}], launcher["dependencies"])
        record = json.loads((self.package / "components/core/stable-launcher-source.json").read_text(encoding="utf-8"))
        self.assertEqual("frozen-0.4.4", record["sourceReleaseId"])
        self.assertEqual(8, record["sourceReleaseSequence"])
        self.assertEqual(hashlib.sha256((source / "release-input.json").read_bytes()).hexdigest(), record["sourceManifestSha256"])
        self.assertEqual("read-after-source-hash-verification", record["lengthEvidence"])
        sbom = json.loads((self.package / "sbom.spdx.json").read_text(encoding="utf-8"))
        self.assertTrue(any(item["fileName"] == "./components/core/stable-launcher-source.json" for item in sbom["files"]))
        result, build, _ = self.powershell("0.4.6", stable_source=source)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(record, build["stableLauncherSource"])

    def test_stable_launcher_rejects_missing_extra_corrupt_and_wrong_version_before_copy(self):
        source = self.stable_fixture()
        launcher = source / "components/launcher/MabiTool.Launcher.dll"
        original = launcher.read_bytes()
        for change in ("missing", "extra", "corrupt", "version"):
            with self.subTest(change=change):
                metadata_path = source / "release-input.json"
                original_metadata = metadata_path.read_bytes()
                if change == "missing":
                    launcher.unlink()
                elif change == "extra":
                    (launcher.parent / "extra.txt").write_text("extra")
                elif change == "corrupt":
                    launcher.write_bytes(b"changed")
                else:
                    metadata_path.write_bytes(original_metadata.replace(b'0.4.4', b'0.4.7'))
                try:
                    result = self.inventory("0.4.6", extra=("--stable-launcher-package", str(source), "--copy-stable-launcher"))
                    self.assertNotEqual(0, result.returncode)
                    self.assertEqual([], list(self.package.iterdir()))
                    if change == "version":
                        result, record, _ = self.powershell("0.4.6", stable_source=source)
                        self.assertNotEqual(0, result.returncode)
                        self.assertIsNone(record)
                finally:
                    launcher.write_bytes(original)
                    (launcher.parent / "extra.txt").unlink(missing_ok=True)
                    metadata_path.write_bytes(original_metadata)

    def test_stable_launcher_rechecks_copy_before_recording_source(self):
        source = self.stable_fixture()
        module = runpy.run_path(str(ROOT / "scripts/build/package-inventory.py"))
        copytree = shutil.copytree
        def corrupt_copy(from_path, to_path):
            copytree(from_path, to_path)
            (to_path / "MabiTool.Launcher.dll").write_bytes(b"bad copy")
        with patch("shutil.copytree", side_effect=corrupt_copy):
            with self.assertRaisesRegex(ValueError, "复制后文件身份不符"):
                module["stable_launcher"](source, self.package)
        self.assertFalse((self.package / "components/core/stable-launcher-source.json").exists())

    def test_publish_only_updater_is_self_contained_and_preserves_dll_entry(self):
        self.fixture()
        harness = self.root / "publication.ps1"
        harness.write_text(r'''
param([string]$Repository, [string]$Fixture, [switch]$UseStable)
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $Repository 'scripts/build/publish-backend.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$loop = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.ForEachStatementAst] -and $node.Variable.Extent.Text -eq '$component' }, $true)[0]
$workRoot = Join-Path $Fixture 'publication'
$Version = '0.4.7'; $RuntimeVersion = '10.0.13'
$env:NUGET_PACKAGES = Join-Path $Fixture 'nuget'
$projects = [ordered]@{ core = 'core'; updater = 'updater'; launcher = 'launcher'; 'room-server' = 'room-server'; 'observation-worker' = 'worker' }
if ($UseStable) { $StableLauncherPackage = 'frozen-source' }
$dotnetCommand = 'CapturePublish'
$script:records = @()
function CapturePublish {
    $script:records += ,@($args)
    $output = $args[$args.IndexOf('-o') + 1]
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $global:LASTEXITCODE = 0
}
& ([scriptblock]::Create($loop.Extent.Text))
$script:records | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Fixture 'publication-arguments.json')
''', encoding="utf-8")
        result = subprocess.run([shutil.which("pwsh") or "powershell", "-NoProfile", "-File", str(harness),
                                 "-Repository", str(ROOT), "-Fixture", str(self.root)], capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(0, result.returncode, result.stderr)
        commands = json.loads((self.root / "publication-arguments.json").read_text(encoding="utf-8-sig"))
        for command in commands:
            with self.subTest(project=command[1]):
                updater = command[1] == "updater"
                self.assertEqual("true" if updater else "false", command[command.index("--self-contained") + 1])
                if updater:
                    self.assertEqual("win-x64", command[command.index("-r") + 1])
                    for argument in ("-p:UseAppHost=true", "-p:PublishReadyToRun=false", "-p:PublishTrimmed=false",
                                     "-p:PublishSingleFile=false", "-p:RuntimeFrameworkVersion=10.0.13"):
                        self.assertIn(argument, command)
        for name in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
            self.assertEqual((self.root / "nuget/microsoft.netcore.app.runtime.win-x64/10.0.13" / name).read_bytes(),
                             (self.root / "publication/components/updater" / name).read_bytes())
        result = subprocess.run([shutil.which("pwsh") or "powershell", "-NoProfile", "-File", str(harness),
                                 "-Repository", str(ROOT), "-Fixture", str(self.root), "-UseStable"], capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(0, result.returncode, result.stderr)
        commands = json.loads((self.root / "publication-arguments.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(["core", "updater", "room-server", "worker"], [command[1] for command in commands])


if __name__ == "__main__":
    unittest.main()
