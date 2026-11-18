"""由完整包 zip 生成应用内更新使用的文件清单与增量包。

输出与完整包同目录：
  <完整包名>.manifest.json                 新版全部程序文件（相对包根的路径、字节数、SHA-256）、完整包与各增量包
  <完整包名>.update-from-<旧版本>.zip        只含新版里内容哈希不在旧版中的文件，每个内容一条、以 SHA-256 命名

旧版本由 --base 给出：旧完整包 zip（读取其中 Moongate/ 下的文件与发行身份），或旧版本的清单 .manifest.json。
旧完整包只读打开；增量包不小于完整包时不生成。已存在同名输出时停止，不覆盖。

用法：python scripts/build/package-update-assets.py --archive artifacts/packages/Moongate-v0.1.2-Windows-x64.zip \
          --base artifacts/packages/Moongate-v0.1.1-Windows-x64.zip
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import zipfile
from pathlib import Path

ROOT = "Moongate/"
PROGRAM_TOPS = ("components/", "licenses/")
ENTRY = "Moongate.exe"
VERSION = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview)?$")
FIXED_TIME = (1980, 1, 1, 0, 0, 0)


def sha256_stream(stream) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    while chunk := stream.read(1 << 20):
        digest.update(chunk)
        size += len(chunk)
    return size, digest.hexdigest()


def sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return sha256_stream(stream)[1]


def read_package(path: Path) -> tuple[str, dict[str, tuple[int, str]]]:
    """完整包内的显示版本与程序文件：相对包根路径 → (字节数, SHA-256)。"""
    files: dict[str, tuple[int, str]] = {}
    with zipfile.ZipFile(path) as archive:
        for info in archive.infolist():
            name = info.filename
            if "\\" in name or not name.startswith(ROOT):
                raise SystemExit(f"完整包条目不在 {ROOT} 下：{name}")
            if name.endswith("/"):
                continue
            relative = name[len(ROOT):]
            if relative != ENTRY and not relative.startswith(PROGRAM_TOPS):
                raise SystemExit(f"完整包条目不是程序文件：{name}")
            if any(part in ("", ".", "..") for part in relative.split("/")):
                raise SystemExit(f"完整包条目路径无效：{name}")
            if relative.lower() in {key.lower() for key in files}:
                raise SystemExit(f"完整包条目重名：{name}")
            with archive.open(info) as stream:
                files[relative] = sha256_stream(stream)
        identity = json.loads(archive.read(ROOT + "components/release.json").decode("utf-8"))
    version = identity.get("displayVersion", "")
    if not VERSION.match(version):
        raise SystemExit(f"发行身份版本无效：{version}")
    if ENTRY not in files:
        raise SystemExit("完整包缺少 Moongate.exe")
    return version, files


def read_base(path: Path) -> tuple[str, set[str]]:
    """旧版本的显示版本与全部内容哈希。"""
    if path.suffix.lower() == ".json":
        manifest = json.loads(path.read_text(encoding="utf-8"))
        if manifest.get("schemaVersion") != 1 or manifest.get("product") != "Moongate":
            raise SystemExit(f"旧版本清单无效：{path}")
        return manifest["displayVersion"], {item["sha256"] for item in manifest["files"]}
    version, files = read_package(path)
    return version, {sha for _, sha in files.values()}


def reserve(path: Path) -> Path:
    if path.exists() or path.with_name(path.name + ".partial").exists():
        raise SystemExit(f"输出已存在：{path}")
    return path.with_name(path.name + ".partial")


def write_delta(archive_path: Path, files: dict[str, tuple[int, str]], wanted: set[str], output: Path) -> None:
    partial = reserve(output)
    by_hash = {}
    for relative, (_, sha) in sorted(files.items()):
        by_hash.setdefault(sha, relative)
    try:
        with zipfile.ZipFile(archive_path) as source, zipfile.ZipFile(partial, "x", zipfile.ZIP_DEFLATED, compresslevel=9) as delta:
            for sha in sorted(wanted):
                info = zipfile.ZipInfo(sha, FIXED_TIME)
                info.compress_type = zipfile.ZIP_DEFLATED
                with source.open(ROOT + by_hash[sha]) as reader, delta.open(info, "w", force_zip64=True) as writer:
                    while chunk := reader.read(1 << 20):
                        writer.write(chunk)
        os.replace(partial, output)
    except BaseException:
        partial.unlink(missing_ok=True)
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--archive", required=True, type=Path, help="新版完整包 zip（同目录须有同名 .sha256）")
    parser.add_argument("--base", action="append", default=[], type=Path, help="旧完整包 zip 或其清单，可重复")
    args = parser.parse_args()

    archive = args.archive.resolve()
    name = archive.name
    version, files = read_package(archive)
    if name != f"Moongate-v{version}-Windows-x64.zip":
        raise SystemExit(f"完整包文件名与发行身份版本 {version} 不符：{name}")
    package_sha = sha256_file(archive)
    checksum = archive.with_name(name + ".sha256")
    if checksum.read_text(encoding="ascii").strip() != f"{package_sha}  {name}":
        raise SystemExit(f".sha256 与完整包不符：{checksum}")
    package_size = archive.stat().st_size
    target_hashes = {sha for _, sha in files.values()}
    manifest_path = archive.with_name(name + ".manifest.json")
    partial = reserve(manifest_path)

    # 先读完全部旧版本再写输出，参数有误时不留下任何文件。
    bases = []
    for base_path in args.base:
        base_version, base_hashes = read_base(base_path.resolve())
        if base_version == version or base_version in {item[0] for item in bases}:
            raise SystemExit(f"旧版本重复或与新版相同：{base_version}")
        reserve(archive.with_name(f"{name}.update-from-{base_version}.zip"))
        bases.append((base_version, base_hashes))

    deltas = []
    outputs = []
    try:
        for base_version, base_hashes in bases:
            wanted = target_hashes - base_hashes
            delta_path = archive.with_name(f"{name}.update-from-{base_version}.zip")
            write_delta(archive, files, wanted, delta_path)
            size = delta_path.stat().st_size
            if size >= package_size:
                delta_path.unlink()
                print(f"从 {base_version} 出发的增量包不小于完整包，未生成", file=sys.stderr)
                continue
            outputs.append(delta_path)
            deltas.append({"fromVersion": base_version, "name": delta_path.name, "size": size,
                           "sha256": sha256_file(delta_path), "blobs": sorted(wanted)})

        manifest = {
            "schemaVersion": 1,
            "product": "Moongate",
            "displayVersion": version,
            "package": {"name": name, "size": package_size, "sha256": package_sha},
            "files": [{"path": path, "size": size, "sha256": sha} for path, (size, sha) in sorted(files.items())],
            "deltas": deltas,
        }
        partial.write_text(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8", newline="\n")
        os.replace(partial, manifest_path)
    except BaseException:
        partial.unlink(missing_ok=True)
        for path in outputs:
            path.unlink(missing_ok=True)
        raise
    outputs.insert(0, manifest_path)
    print(json.dumps({"manifest": manifest_path.name, "files": len(files),
                      "deltas": [{k: item[k] for k in ("fromVersion", "name", "size")} | {"blobs": len(item["blobs"])} for item in deltas],
                      "assets": [str(path) for path in outputs]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
