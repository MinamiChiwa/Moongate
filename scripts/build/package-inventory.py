"""按实际目录包生成发行身份、来源、许可与 SPDX 2.3 清单。"""
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


def tree_sha256(directory):
    """组件树哈希：全部文件按相对路径（/ 分隔）的序号顺序排列，对每行“相对路径、制表符、小写 SHA-256、换行”求 SHA-256。
    与 src/PortableLauncher/Update/ReleaseIdentity.cs 的 TreeSha256 一致。"""
    entries = []
    for path in directory.rglob("*"):
        if path.is_symlink() or path.is_junction():
            raise ValueError("组件包含链接")
        if path.is_file():
            entries.append((path.relative_to(directory).as_posix(), digest(path)))
    entries.sort(key=lambda item: item[0].encode("utf-16-be"))
    text = "".join(f"{name}\t{value}\n" for name, value in entries)
    return hashlib.sha256(text.encode("utf-8")).hexdigest(), len(entries)


def verify_game_cli(repository, component):
    """组件 game-cli：目录里只有登记的命令行工具，长度与 SHA-256 等于 packaging/game-cli/game-cli.lock.json。
    发行构建总是带该组件；清单检查对不带的包（此前的发行包）同样放行。"""
    lock = read(repository / "packaging/game-cli/game-cli.lock.json")
    names = sorted(path.relative_to(component).as_posix() for path in component.rglob("*"))
    if names != [lock["fileName"]]:
        raise ValueError(f"命令行工具组件内容与登记不符，应只含 {lock['fileName']}：" + "、".join(names))
    executable = component / lock["fileName"]
    if (executable.is_symlink() or executable.is_junction() or not executable.is_file()
            or executable.stat().st_size != lock["length"] or digest(executable) != lock["sha256"]):
        raise ValueError(f"随包命令行工具的长度或 SHA-256 与 packaging/game-cli/game-cli.lock.json 的登记不符：{lock['fileName']}")
    return lock


def verify_life_vision(repository, component):
    """组件 life-vision：templates/ 里只有登记的模板图片与登记文件，长度与 SHA-256 等于 workers/life-vision/Assets/templates.lock.json。"""
    lock_path = repository / "workers/life-vision/Assets/templates.lock.json"
    lock = read(lock_path)
    expected = {item["path"]: item for item in lock["files"]}
    shipped = sorted(path.relative_to(component).as_posix() for path in (component / "templates").rglob("*") if path.is_file())
    if shipped != sorted([*expected, "templates/templates.lock.json"]):
        raise ValueError("钓鱼识别组件的模板与登记不符：" + "、".join(shipped))
    for relative, item in expected.items():
        image = component / relative
        if image.is_symlink() or image.is_junction() or image.stat().st_size != item["length"] or digest(image) != item["sha256"]:
            raise ValueError(f"随包模板图片的长度或 SHA-256 与 templates.lock.json 的登记不符：{relative}")
    if digest(component / "templates/templates.lock.json") != digest(lock_path):
        raise ValueError("随包 templates.lock.json 与仓库登记文件不一致")
    return lock


def write_release_identity(package, components_root, component_ids, arguments):
    """components/release.json：宿主据此得知自己的版本，更新时用它核对下载包与暂存目录。"""
    components = []
    for component in component_ids:
        tree, count = tree_sha256(components_root / component)
        components.append(dict(id=component, files=count, treeSha256=tree))
    display = arguments.version + ("-preview" if arguments.channel == "preview" else "")
    write(components_root / "release.json", dict(
        schemaVersion=1, product="Moongate", version=arguments.version, channel=arguments.channel, displayVersion=display,
        repository=arguments.repository_name, builtAt=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        entry=dict(path="Moongate.exe", sha256=digest(package / "Moongate.exe")), components=components))


def inventory(repository, package, arguments):
    if not re.fullmatch(r"(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})", arguments.version):
        raise ValueError("发行版本须为三段非负整数")
    if arguments.channel not in ("stable", "preview"):
        raise ValueError("发行渠道须为 stable 或 preview")
    if not re.fullmatch(r"[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}", arguments.repository_name):
        raise ValueError("发行仓库须为 owner/name")
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
    extra = sorted({path.name for path in package.iterdir()} - {"Moongate.exe", "components", "sbom.spdx.json", "THIRD_PARTY_NOTICES.txt"})
    if extra:
        raise ValueError("发行目录根有多余内容：" + "、".join(extra))
    component_ids = sorted(path.name for path in components_root.iterdir() if path.is_dir())
    required = {"core", "room-server", "dotnet-runtime", "desktop"}
    if not required.issubset(component_ids) or set(component_ids) - required - {"observation-worker", "color-engine", "game-cli", "life-vision"}:
        raise ValueError("发行组件闭包无效")
    game_cli = verify_game_cli(repository, components_root / "game-cli") if "game-cli" in component_ids else None
    life_vision = verify_life_vision(repository, components_root / "life-vision") if "life-vision" in component_ids else None
    nuget = Path(arguments.nuget_packages).expanduser()
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

    # nuspec 没有许可表达式的包按登记的人工映射标注，映射同时固定包内许可文件的哈希作为依据。
    overrides_path = repository / "packaging/licenses/nuget-license-overrides.json"
    license_overrides = {entry["id"].lower(): entry for entry in read(overrides_path)["packages"]} if overrides_path.is_file() else {}
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
        override = license_overrides.get(normalized) if declared == "NOASSERTION" else None
        if override:
            evidence = location / override["licenseFile"]
            if not evidence.is_file() or digest(evidence) != override["licenseSha256"]:
                raise ValueError(f"许可映射的依据文件与登记不符，请重新核对：{name}/{version} {override['licenseFile']}")
            declared = override["license"]
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
                                comment=override["evidence"] if override else None,
                                checksums=[dict(algorithm="SHA256", checksumValue=digest(nupkg))] if nupkg.is_file() else None)
        relationships.append(dict(spdxElementId=own_ref, relationshipType="DEPENDS_ON", relatedSpdxElement=reference))
        basis = f"License basis: {override['evidence']}\n" if override else ""
        license_text = f"{name} {version}\n{copyright}\nLicense: {declared}\n{basis}" + "\n".join(actual_texts)
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
            # Apache-2.0 第 4(b) 条：修改过的文件须写明已修改。
            stripped = [path for path in distribution["files"]
                        if any(item["path"] == path and item.get("debugSectionsRemoved") for item in tesseract["files"])]
            modification = "、".join(stripped) + " 已移除调试信息（.debug_* 节、COFF 符号表与签名），代码与数据未改动" if stripped else ""
            reference = add_package(distribution["name"], distribution["version"] or "NOASSERTION", distribution["license"],
                                    location=distribution["source"],
                                    comment=f"染色组件随包 Tesseract {tesseract['version']}（{tesseract['distribution']['name']}）；文件："
                                            + "、".join(distribution["files"]) + ("；" + modification if stripped else ""))
            relationships.append(dict(spdxElementId=own_ref, relationshipType="DEPENDS_ON", relatedSpdxElement=reference))
            notices.append(f"{distribution['name']} {distribution['version'] or ''}\nLicense: {distribution['license']}\n"
                           f"Source: {distribution['source']}\n" + (f"Modified: {modification}\n" if stripped else "") + text)

    if game_cli:
        # 专有二进制：权利归 Nexon / devCAT，由发行者原样随包分发；没有随附的许可文字，声明正文取自登记文件。
        cli_text = (f"{game_cli['fileName']} {game_cli['version']}\n来源：{game_cli['source']}\nSHA-256：{game_cli['sha256']}\n"
                    f"长度：{game_cli['length']} 字节\n版权归属：{game_cli['vendor']}\n{game_cli['copyright']}\n{game_cli['distribution']}\n")
        cli_license = license_reference("Nexon-devCAT-Proprietary", cli_text)
        cli_ref = add_package(game_cli["fileName"], game_cli["version"], cli_license, location="NOASSERTION",
                              copyright=f"Copyright {game_cli['vendor']}",
                              comment=f"{game_cli['source']}{game_cli['distribution']}",
                              checksums=[dict(algorithm="SHA256", checksumValue=game_cli["sha256"])])
        relationships.append(dict(spdxElementId=own_ref, relationshipType="DEPENDS_ON", relatedSpdxElement=cli_ref))
        notices.append(cli_text)

    if life_vision:
        # 钓鱼识别模板是台服客户端界面裁图，权利归 Nexon / devCAT，由发行者原样随包分发；声明正文取自登记文件，逐张列出哈希。
        templates_text = (f"钓鱼识别模板（{len(life_vision['files'])} 张）\n来源：{life_vision['source']}\n版权归属：{life_vision['vendor']}\n"
                          f"{life_vision['copyright']}\n{life_vision['distribution']}\n"
                          + "".join(f"{item['path']}  SHA-256 {item['sha256']}  {item['length']} 字节  {item['role']}\n" for item in life_vision["files"]))
        templates_license = license_reference("Nexon-devCAT-FishingTemplates", templates_text)
        templates_ref = add_package("life-vision-templates", "NOASSERTION", templates_license, location="NOASSERTION",
                                    copyright=f"Copyright {life_vision['vendor']}", comment=f"{life_vision['source']}{life_vision['distribution']}")
        relationships.append(dict(spdxElementId=own_ref, relationshipType="DEPENDS_ON", relatedSpdxElement=templates_ref))
        notices.append(templates_text)

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
        # 桌面前端内嵌的游戏图标：取自台服客户端的权利归 Nexon / devCAT，自绘的随 Moongate 以 MIT 发布；登记文件随包保存，声明逐个列出哈希。
        icons_lock = repository / "frontends/desktop/src/MabiTool.Desktop/Assets/GameIcons/game-icons.lock.json"
        icons = read(icons_lock)
        shutil.copy2(icons_lock, licenses / "game-icons.lock.json")
        game_icons = [item for item in icons["files"] if item["author"] != "自绘"]
        icons_text = (f"桌面游戏图标（{len(icons['files'])} 个，其中取自客户端 {len(game_icons)} 个、自绘 {len(icons['files']) - len(game_icons)} 个）\n"
                      f"来源：{icons['source']}\n版权归属：{icons['vendor']}\n{icons['copyright']}\n{icons['distribution']}\n"
                      + "".join(f"{item['path']}  SHA-256 {item['sha256']}  {item['length']} 字节  {item['author']}  {item['role']}\n" for item in icons["files"]))
        icons_license = license_reference("Nexon-devCAT-GameIcons", icons_text)
        icons_ref = add_package("desktop-game-icons", "NOASSERTION", icons_license, location="NOASSERTION",
                                copyright=f"Copyright {icons['vendor']}（自绘图标 Copyright (c) 2026 MinamiChiwa（南千和），MIT）",
                                comment=f"{icons['source']}{icons['distribution']}")
        relationships.append(dict(spdxElementId=own_ref, relationshipType="DEPENDS_ON", relatedSpdxElement=icons_ref))
        notices.append(icons_text)

    notices_text = "\n\n".join(notices) + "\n"
    # 包根的这份在解压版里位于 licenses/，components/core 内另存一份随组件发行。
    (package / "THIRD_PARTY_NOTICES.txt").write_text(notices_text, encoding="utf-8")
    (components_root / "core/THIRD_PARTY_NOTICES.txt").write_text(notices_text, encoding="utf-8")
    write(components_root / "core/source-inventory.json", {"schemaVersion": 1, "ownLicense": "MIT", "sources": source_records,
          "features": source_baseline["features"],
          "runtimeDistributions": [dict(componentId="dotnet-runtime", version=arguments.runtime_version, runtimeMode="framework-dependent")],
          "externalDependencies": [{"id": "npcap", "bundled": False}] if "observation-worker" in component_ids else []})
    # 发行身份写在 SBOM 之前，自身也列入 SBOM；树哈希只覆盖组件目录，不含身份文件本身。
    write_release_identity(package, components_root, component_ids, arguments)
    files = []
    for path in [launcher, *sorted(components_root.rglob("*"))]:
        if path.is_symlink() or path.is_junction():
            raise ValueError("组件包含链接")
        if path.suffix.lower() == ".pdb":
            raise ValueError("发行组件不含调试符号：" + path.relative_to(package).as_posix())
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
    print(f"已登记 {len(component_ids)} 个组件、{len(packages)} 项来源/依赖和 {len(files)} 个发行文件")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True, type=Path)
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--runtime-version", required=True)
    parser.add_argument("--release-id", required=True)
    parser.add_argument("--channel", required=True)
    parser.add_argument("--repository-name", default="MinamiChiwa/Moongate")
    parser.add_argument("--nuget-packages", type=Path, default=Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages")))
    arguments = parser.parse_args()
    inventory(arguments.repository.resolve(), arguments.package.resolve(), arguments)


if __name__ == "__main__":
    main()
