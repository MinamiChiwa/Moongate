"""按实际目录包生成来源、许可、SPDX 2.3 和签名发行输入。"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import uuid
import xml.etree.ElementTree as ET


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def identifier(value):
    return "SPDXRef-" + re.sub(r"[^A-Za-z0-9.-]", "-", value)


def launcher_version(component):
    libraries = read(component / "MabiTool.Launcher.deps.json")["libraries"]
    versions = [name.split("/", 1)[1] for name in libraries if name.startswith("MabiTool.Launcher/")]
    if len(versions) != 1 or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", versions[0]):
        raise ValueError("launcher 实际版本无效")
    return versions[0]


def launcher_files(root):
    for path in (root, *root.parents):
        if path.is_symlink() or path.is_junction():
            raise ValueError("launcher 来源包含链接")
    pending = [root]
    files = {}
    names = set()
    while pending:
        for path in pending.pop().iterdir():
            if path.is_symlink() or path.is_junction():
                raise ValueError("launcher 来源包含链接")
            name = path.relative_to(root).as_posix()
            if name.lower() in names:
                raise ValueError("launcher 文件路径冲突")
            names.add(name.lower())
            if path.is_dir():
                pending.append(path)
            elif path.is_file():
                files[name] = {"sizeBytes": path.stat().st_size, "sha256": digest(path)}
            else:
                raise ValueError("launcher 文件类型无效")
            if len(names) > 100_000:
                raise ValueError("launcher 来源文件过多")
    return files


def stable_launcher(source, destination=None):
    root = source / "components/launcher"
    actual = launcher_files(root)
    manifest = read(source / "release-input.json")
    sbom_path = source / "sbom.spdx.json"
    component = next(item for item in manifest["components"] if item["componentId"] == "launcher")
    if component["directory"] != "components/launcher" or manifest["releaseSequence"] < 1 or not manifest["releaseId"]:
        raise ValueError("稳定 launcher 发行来源无效")
    expected = {}
    for entry in read(sbom_path)["files"]:
        name = entry["fileName"].removeprefix("./")
        if name.startswith("components/launcher/"):
            relative = name.removeprefix("components/launcher/")
            checksums = [item["checksumValue"].lower() for item in entry["checksums"] if item["algorithm"] == "SHA256"]
            if relative in expected or len(checksums) != 1 or not re.fullmatch(r"[0-9a-f]{64}", checksums[0]):
                raise ValueError("稳定 launcher SBOM 身份无效")
            expected[relative] = checksums[0]
    if not expected or set(expected) != set(actual) or any(actual[name]["sha256"] != value for name, value in expected.items()):
        raise ValueError("稳定 launcher 文件集合或 SHA256 与原 SBOM 不符")
    version = launcher_version(root)
    if version != component["version"]:
        raise ValueError("稳定 launcher 组件版本与实际依赖元数据不符")
    record = dict(schemaVersion=1, sourceReleaseId=manifest["releaseId"], sourceReleaseSequence=manifest["releaseSequence"],
                  sourceManifestSha256=digest(source / "release-input.json"), sourceSbomSha256=digest(sbom_path),
                  launcherVersion=version, lengthEvidence="read-after-source-hash-verification", files=actual)
    if destination is not None:
        target = destination / "components/launcher"
        for path in (target, *target.parents):
            if path.is_symlink() or path.is_junction():
                raise ValueError("launcher 复制目标包含链接")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(root, target)
        if launcher_files(target) != actual:
            raise ValueError("稳定 launcher 复制后文件身份不符")
        core = destination / "components/core"
        core.mkdir(exist_ok=True)
        write(core / "stable-launcher-source.json", record)
    return record


def verify_updater_runtime(component, runtime_version, nuget):
    for name in ("MabiTool.Updater.dll", "MabiTool.Updater.exe"):
        if not (component / name).is_file() or (component / name).stat().st_size == 0:
            raise ValueError(f"updater 缺少 DLL 入口或 apphost：{name}")
    options = read(component / "MabiTool.Updater.runtimeconfig.json")["runtimeOptions"]
    expected = [{"name": "Microsoft.NETCore.App", "version": runtime_version}]
    if options.get("includedFrameworks") != expected or options.get("framework") or options.get("frameworks"):
        raise ValueError("updater 须包含指定版本的独立 .NET 运行时")
    deps = read(component / "MabiTool.Updater.deps.json")
    if not deps["runtimeTarget"]["name"].endswith("/win-x64") or not any(
        name in (f"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/{runtime_version}",
                 f"Microsoft.NETCore.App.Runtime.win-x64/{runtime_version}") and metadata.get("type") == "runtimepack"
        for name, metadata in deps["libraries"].items()
    ):
        raise ValueError("updater 运行时包版本或架构不符")
    pack = nuget / "microsoft.netcore.app.runtime.win-x64" / runtime_version
    for name, directory in (("hostpolicy.dll", "native"), ("coreclr.dll", "native"),
                            ("System.Private.CoreLib.dll", "lib/net10.0"), ("System.Runtime.dll", "lib/net10.0")):
        actual = component / name
        source = pack / "runtimes/win-x64" / directory / name
        if not actual.is_file() or not source.is_file() or digest(actual) != digest(source):
            raise ValueError(f"updater 内嵌运行时内容与 {runtime_version} 不符：{name}")
    for name in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
        if not (component / name).is_file() or digest(component / name) != digest(pack / name):
            raise ValueError(f"updater 缺少原始运行时许可：{name}")
    return pack


def inventory(repository, package, arguments):
    minimum = arguments.minimum_updater_version
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", minimum) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", arguments.version):
        raise ValueError("发行版本和最低更新器版本须为三段非负整数")
    if tuple(map(int, minimum.split("."))) > tuple(map(int, arguments.version.split("."))):
        raise ValueError("最低更新器版本高于本次发行的 updater 版本")
    if not package.is_relative_to(repository / "artifacts"):
        raise ValueError("发行输出须位于仓库 artifacts")
    for path in (package, *package.parents):
        if path.is_symlink() or path.is_junction():
            raise ValueError("发行输出包含链接目录")
    components_root = package / "components"
    if not components_root.is_dir():
        raise ValueError("发行目录缺少 components")
    # 包根只有入口 Moongate.exe 与 components/（再次生成时另有本工具上次写出的清单）；解压版 zip 照此布局打包。
    launcher = package / "Moongate.exe"
    if not launcher.is_file() or launcher.is_symlink():
        raise ValueError("发行目录缺少包根入口 Moongate.exe")
    extra = sorted({path.name for path in package.iterdir()} - {"Moongate.exe", "components", "release-input.json", "sbom.spdx.json", "THIRD_PARTY_NOTICES.txt"})
    if extra:
        raise ValueError("发行目录根有多余内容：" + "、".join(extra))
    component_ids = sorted(path.name for path in components_root.iterdir() if path.is_dir())
    required = {"core", "updater", "launcher", "room-server", "dotnet-runtime", "desktop"}
    if not required.issubset(component_ids) or set(component_ids) - required - {"observation-worker", "color-engine"}:
        raise ValueError("发行组件闭包无效")
    target = read(components_root / "core/install-target.json")
    if target["hostVersion"] != arguments.version or target["runtimeMode"] != "framework-dependent":
        raise ValueError("发行目标与实际宿主不符")
    nuget = Path(arguments.nuget_packages).expanduser()
    updater = components_root / "updater"
    updater_runtime_pack = verify_updater_runtime(updater, arguments.runtime_version, nuget)
    actual_launcher_version = launcher_version(components_root / "launcher")
    stable_source_path = components_root / "core/stable-launcher-source.json"
    stable_source = read(stable_source_path) if stable_source_path.exists() else None
    if stable_source and (stable_source["launcherVersion"] != actual_launcher_version or
                          stable_source["files"] != launcher_files(components_root / "launcher")):
        raise ValueError("稳定 launcher 来源记录与实际组件不符")
    if not stable_source and actual_launcher_version != arguments.version:
        raise ValueError("新发布 launcher 版本与发行版本不符")
    licenses = components_root / "core/licenses"
    licenses.mkdir(exist_ok=True)
    own = repository / "packaging/licenses/MinamiChiwa-MIT.txt"
    original = repository / "LICENSE"
    shutil.copy2(own, licenses / "MinamiChiwa-MIT.txt")
    shutil.copy2(original, licenses / "MobiFolio-MIT.txt")
    source_baseline = read(repository / "scripts/maintenance/source-baselines.json")
    sources = source_baseline["sources"]
    source_records = []
    packages, extracted, relationships = [], [], []
    notices = ["Moongate 发行来源与第三方声明", own.read_text(encoding="utf-8"), original.read_text(encoding="utf-8")]

    def add_package(name, version, declared, concluded=None, location="NOASSERTION", copyright="NOASSERTION", comment=None, checksums=None):
        reference = identifier(f"{name}-{version}")
        item = dict(SPDXID=reference, name=name, versionInfo=version, downloadLocation=location, filesAnalyzed=False,
                    licenseDeclared=declared, licenseConcluded=concluded or declared, copyrightText=copyright)
        if comment:
            item["comment"] = comment
        if checksums:
            item["checksums"] = checksums
        packages.append(item)
        relationships.append(dict(spdxElementId="SPDXRef-DOCUMENT", relationshipType="DESCRIBES", relatedSpdxElement=reference))
        return reference

    def license_reference(name, text):
        reference = "LicenseRef-" + re.sub(r"[^A-Za-z0-9.-]", "-", name)
        extracted.append(dict(licenseId=reference, extractedText=text, name=name))
        return reference

    own_ref = add_package("Moongate", arguments.version, "MIT", copyright="Copyright (c) 2026 MinamiChiwa（南千和）",
                          comment="统一新增代码；各来源和分发依赖另列")
    for source in sources:
        distribution = source.get("distributionLicense")
        if source["licenseStatus"] == "MIT":
            declared = "MIT"
        elif distribution:
            if distribution.get("authorizationSource") != "user" or digest(repository / distribution["path"]) != distribution["sha256"]:
                raise ValueError(f"{source['id']} 发行授权证据不符")
            declared = distribution["id"]
        else:
            declared = "NOASSERTION"
        record = {key: source[key] for key in ("id", "repository", "commit", "licenseStatus", "licenseEvidence")}
        if distribution:
            record["distributionLicense"] = distribution
            license_path = repository / distribution["path"]
            shutil.copy2(license_path, licenses / license_path.name)
            notices.append(license_path.read_text(encoding="utf-8-sig"))
        if source.get("workingTree"):
            record["workingTree"] = source["workingTree"]
            record["files"] = source["files"]
        if source.get("authors"):
            record["authors"] = source["authors"]
        if source.get("pullRequests"):
            record["pullRequests"] = [{key: pull[key] for key in ("number", "baseCommit", "headCommit", "authors", "disposition") if key in pull}
                                      for pull in source["pullRequests"] if pull["disposition"] in ("included-in-main", "adopted-function")]
        source_records.append(record)
        source_version = source["commit"] + ("+working-tree." + source["workingTree"]["sha256"][:12] if source.get("workingTree") else "")
        reference = add_package(source["id"], source_version, declared,
                                location=f"https://github.com/{source['repository']}/tree/{source['commit']}",
                                copyright=distribution["copyright"] if distribution else ", ".join(source.get("authors", [])) or "NOASSERTION")
        relationships.append(dict(spdxElementId=own_ref, relationshipType="GENERATED_FROM", relatedSpdxElement=reference))
        notices.append(json.dumps(record, ensure_ascii=False, indent=2))

    runtime = components_root / "dotnet-runtime"
    runtime_text = (runtime / "LICENSE.txt").read_text(encoding="utf-8-sig") + "\n" + (runtime / "ThirdPartyNotices.txt").read_text(encoding="utf-8-sig")
    runtime_license = license_reference("Dotnet-Distribution", runtime_text)
    add_package("Microsoft.NET-and-ASP.NET-Runtime", arguments.runtime_version, "MIT", runtime_license,
                "https://dotnet.microsoft.com/download/dotnet/10.0", "Microsoft and third-party contributors",
                "随包共享框架，原始 LICENSE.txt 和 ThirdPartyNotices.txt 保存在 dotnet-runtime 组件")
    notices.append(runtime_text)

    updater_runtime_text = (updater / "LICENSE.TXT").read_text(encoding="utf-8-sig") + "\n" + (updater / "THIRD-PARTY-NOTICES.TXT").read_text(encoding="utf-8-sig")
    updater_runtime_license = license_reference("Dotnet-Updater-Distribution", updater_runtime_text)
    updater_runtime_ref = add_package("Microsoft.NETCore.App.Runtime.win-x64", arguments.runtime_version, "MIT", updater_runtime_license,
                f"https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/{arguments.runtime_version}",
                "Microsoft and third-party contributors", "updater 组件内嵌独立运行时；原始许可保存在组件根目录",
                checksums=[dict(algorithm="SHA256", checksumValue=digest(updater_runtime_pack / f"microsoft.netcore.app.runtime.win-x64.{arguments.runtime_version}.nupkg"))]
                if (updater_runtime_pack / f"microsoft.netcore.app.runtime.win-x64.{arguments.runtime_version}.nupkg").is_file() else None)
    relationships.append(dict(spdxElementId=own_ref, relationshipType="DEPENDS_ON", relatedSpdxElement=updater_runtime_ref))
    notices.append(updater_runtime_text)

    dependencies = {}
    for component in component_ids:
        for deps in (components_root / component).glob("*.deps.json"):
            for key, value in read(deps).get("libraries", {}).items():
                if value.get("type") == "package":
                    name, version = key.rsplit("/", 1)
                    dependencies[(name.lower(), version)] = name
    for (normalized, version), name in sorted(dependencies.items()):
        location = nuget / normalized / version
        nuspec = location / f"{normalized}.nuspec"
        if not nuspec.is_file():
            raise ValueError(f"NuGet 缓存缺少许可元数据：{name}/{version}")
        metadata = ET.parse(nuspec).getroot()
        def field(tag):
            return next((entry for entry in metadata.iter() if entry.tag.rsplit("}", 1)[-1] == tag), None)
        license_node, copyright_node = field("license"), field("copyright")
        declared = license_node.text if license_node is not None and license_node.get("type") == "expression" else "NOASSERTION"
        copyright = copyright_node.text if copyright_node is not None else "NOASSERTION"
        destination = licenses / "nuget" / f"{name}-{version}"
        destination.mkdir(parents=True, exist_ok=True)
        shutil.copy2(nuspec, destination / nuspec.name)
        actual_texts = []
        for candidate in location.iterdir():
            if candidate.is_file() and any(token in candidate.name.lower() for token in ("license", "notice", "copying", "copyright")):
                shutil.copy2(candidate, destination / candidate.name)
                actual_texts.append(candidate.read_text(encoding="utf-8-sig", errors="replace"))
        if declared == "MIT":
            actual_texts.append(own.read_text(encoding="utf-8").replace("Copyright (c) 2026 MinamiChiwa（南千和）", copyright or "Copyright: see package metadata"))
        if declared == "Apache-2.0" and not actual_texts:
            actual_texts.append("Apache-2.0 完整授权文字见 dotnet-runtime/ThirdPartyNotices.txt。")
        nupkg = location / f"{normalized}.{version}.nupkg"
        reference = add_package(name, version, declared, location=f"https://www.nuget.org/packages/{name}/{version}", copyright=copyright,
                                checksums=[dict(algorithm="SHA256", checksumValue=digest(nupkg))] if nupkg.is_file() else None)
        relationships.append(dict(spdxElementId=own_ref, relationshipType="DEPENDS_ON", relatedSpdxElement=reference))
        license_text = f"{name} {version}\n{copyright}\nLicense: {declared}\n" + "\n".join(actual_texts)
        (destination / "NOTICE.txt").write_text(license_text, encoding="utf-8")
        notices.append(license_text)

    if "color-engine" in component_ids:
        color = components_root / "color-engine"
        color_record = read(color / "runtime-build.json")
        python_text = (color / "python/LICENSE.txt").read_text(encoding="utf-8-sig")
        python_license = license_reference("CPython-Distribution", python_text)
        add_package("CPython", color_record["pythonVersion"], "PSF-2.0", python_license,
                    f"https://www.python.org/downloads/release/python-{color_record['pythonVersion'].replace('.', '')}/")
        notices.append(python_text)
        for distribution in color_record["packages"]:
            texts = [(color / relative).read_text(encoding="utf-8-sig", errors="replace") for relative in distribution["licenseFiles"]]
            if distribution["license"] and len(distribution["license"]) > 150:
                texts.append(distribution["license"])
            expression = distribution["licenseExpression"]
            declared = expression or (license_reference("Python-" + distribution["name"], "\n".join(texts)) if texts else "NOASSERTION")
            add_package(distribution["name"], distribution["version"], declared,
                        location=f"https://pypi.org/project/{distribution['name']}/{distribution['version']}/",
                        copyright=distribution["author"] or "NOASSERTION")
            notices.append(f"{distribution['name']} {distribution['version']}\n" + "\n".join(texts))
        tesseract = color_record["tesseract"]
        for item in tesseract["files"]:
            if digest(color / item["path"]) != item["sha256"]:
                raise ValueError(f"随包 Tesseract 文件与构建记录不符：{item['path']}")
        for distribution in tesseract["packages"]:
            license_path = color / distribution["licenseFile"]
            if digest(license_path) != distribution["licenseSha256"]:
                raise ValueError(f"随包 Tesseract 许可文字与构建记录不符：{distribution['id']}")
            text = license_path.read_text(encoding="utf-8-sig", errors="replace")
            reference = add_package(distribution["name"], distribution["version"] or "NOASSERTION", distribution["license"],
                                    location=distribution["source"],
                                    comment=f"染色组件随包 Tesseract {tesseract['version']}（{tesseract['distribution']['name']}）；文件："
                                            + "、".join(distribution["files"]))
            relationships.append(dict(spdxElementId=own_ref, relationshipType="DEPENDS_ON", relatedSpdxElement=reference))
            notices.append(f"{distribution['name']} {distribution['version'] or ''}\nLicense: {distribution['license']}\n"
                           f"Source: {distribution['source']}\n{text}")

    if "desktop" in component_ids:
        # 桌面前端内嵌 Noto Sans SC/TC 子集字体（SIL OFL 1.1），许可文字随包保存并写入声明。
        fonts_license = repository / "frontends/desktop/src/MabiTool.Desktop/Assets/Fonts/OFL.txt"
        shutil.copy2(fonts_license, licenses / "NotoSans-OFL.txt")
        fonts_text = fonts_license.read_text(encoding="utf-8-sig")
        fonts_ref = add_package("Noto Sans SC and TC (subset)", "NOASSERTION", "OFL-1.1", location="https://github.com/notofonts/noto-cjk",
                                copyright="Copyright 2014-2021 Adobe (http://www.adobe.com/)",
                                comment="桌面前端内嵌的三字重子集，由 scripts/build/subset-desktop-fonts.py 从可变字体生成")
        relationships.append(dict(spdxElementId=own_ref, relationshipType="DEPENDS_ON", relatedSpdxElement=fonts_ref))
        notices.append("Noto Sans SC / Noto Sans TC (desktop frontend fonts)\nLicense: OFL-1.1\n" + fonts_text)

    notices_text = "\n\n".join(notices) + "\n"
    # 包根的这份在解压版里位于 licenses/，components/core 内另存一份随组件发行。
    (package / "THIRD_PARTY_NOTICES.txt").write_text(notices_text, encoding="utf-8")
    (components_root / "core/THIRD_PARTY_NOTICES.txt").write_text(notices_text, encoding="utf-8")
    write(components_root / "core/source-inventory.json", {"schemaVersion": 1, "ownLicense": "MIT", "sources": source_records,
          "features": source_baseline["features"],
          "stableLauncherSource": stable_source,
          "runtimeDistributions": [dict(componentId="dotnet-runtime", version=arguments.runtime_version, runtimeMode="framework-dependent"),
                                   dict(componentId="updater", version=arguments.runtime_version, runtimeMode="self-contained",
                                        runtimePackage="Microsoft.NETCore.App.Runtime.win-x64", licenseFiles=["LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"])],
          "externalDependencies": [{"id": "npcap", "bundled": False}] if "observation-worker" in component_ids else []})
    files = []
    for path in [launcher, *sorted(components_root.rglob("*"))]:
        if path.is_symlink() or path.is_junction():
            raise ValueError("组件包含链接")
        if path.is_file():
            relative = path.relative_to(package).as_posix()
            reference = identifier(relative)
            files.append(dict(SPDXID=reference, fileName="./" + relative, checksums=[dict(algorithm="SHA256", checksumValue=digest(path))],
                              licenseConcluded="NOASSERTION", copyrightText="NOASSERTION"))
            relationships.append(dict(spdxElementId=own_ref, relationshipType="CONTAINS", relatedSpdxElement=reference))
    now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    write(package / "sbom.spdx.json", dict(spdxVersion="SPDX-2.3", dataLicense="CC0-1.0", SPDXID="SPDXRef-DOCUMENT",
          name=arguments.release_id, documentNamespace=f"https://mabitool.local/spdx/{arguments.release_id}/{uuid.uuid4()}",
          creationInfo=dict(creators=["Tool: MabiTool-package-inventory"], created=now), packages=packages,
          files=files, relationships=relationships, hasExtractedLicensingInfos=extracted))
    schemas = {}
    for migration in target["migrations"]:
        schemas[migration["moduleId"]] = max(schemas.get(migration["moduleId"], 0), migration["version"])
    component_inputs = []
    for component in component_ids:
        runtime_component = component == "dotnet-runtime"
        component_inputs.append(dict(componentId=component, directory=f"components/{component}",
            version=arguments.runtime_version if runtime_component else actual_launcher_version if component == "launcher" else arguments.version,
            optional=component in ("color-engine", "observation-worker"), moduleApiRange=dict(minimum=1, maximum=2),
            hostVersionRange=dict(minimum="0.1.0", maximum="1.0.0"), workerProtocolRange=dict(minimum=1, maximum=2),
            dataSchemaRange=dict(minimum=0, maximum=max(schemas.values()) + 1),
            dependencies=[] if runtime_component or component in ("color-engine", "updater") else [dict(componentId="dotnet-runtime", minimumVersion=arguments.runtime_version)]))
    write(package / "release-input.json", dict(schemaVersion=1, productId="mabitool", channel=arguments.channel,
          releaseId=arguments.release_id, version=arguments.version, releaseSequence=arguments.release_sequence, publishedAt=now,
          minUpdaterVersion=minimum, architecture="x64", minimumOsBuild="10.0.19045", runtimeMode="framework-dependent",
          releaseNotes="", sbomPath="sbom.spdx.json", noticesPath="THIRD_PARTY_NOTICES.txt", components=component_inputs,
          data=[dict(moduleId=module, schemaFrom=version, schemaTo=version, oldReadableSchemaRange=dict(minimum=version, maximum=version + 1),
                     migrationId=f"{module}-schema-{version}", requiresFullBackup=False) for module, version in sorted(schemas.items())]))
    print(f"已登记 {len(component_ids)} 个组件、{len(packages)} 项来源/依赖和 {len(files)} 个发行文件")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True, type=Path)
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--minimum-updater-version", default="0.1.0")
    parser.add_argument("--stable-launcher-package", type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--verify-stable-launcher", action="store_true")
    mode.add_argument("--copy-stable-launcher", action="store_true")
    parser.add_argument("--runtime-version", required=True)
    parser.add_argument("--release-id", required=True)
    parser.add_argument("--release-sequence", required=True, type=int)
    parser.add_argument("--channel", required=True)
    parser.add_argument("--nuget-packages", type=Path, default=Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages")))
    arguments = parser.parse_args()
    if arguments.verify_stable_launcher or arguments.copy_stable_launcher:
        if arguments.stable_launcher_package is None:
            parser.error("稳定 launcher 校验或复制须指定 --stable-launcher-package")
        destination = arguments.package.absolute() if arguments.copy_stable_launcher else None
        if destination is not None and not destination.is_relative_to(arguments.repository.resolve() / "artifacts"):
            parser.error("launcher 复制输出须位于仓库 artifacts")
        stable_launcher(arguments.stable_launcher_package.absolute(), destination)
        return
    inventory(arguments.repository.resolve(), arguments.package.resolve(), arguments)


if __name__ == "__main__":
    main()
