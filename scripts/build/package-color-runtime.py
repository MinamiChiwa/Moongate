"""从固定依赖构建可搬移的隔离 Python 染色组件。"""
import argparse
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def command(arguments, **kwargs):
    return subprocess.run(arguments, check=True, text=True, **kwargs)


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def safe_output(path):
    path = path.absolute()
    allowed = (ROOT / "artifacts").resolve()
    if not path.is_relative_to(allowed) or path.exists():
        raise ValueError("组件输出须为 artifacts 内的新目录")
    for parent in (path, *path.parents):
        if parent.exists() and (parent.is_symlink() or parent.is_junction()):
            raise ValueError("组件输出包含链接目录")
    return path


def bundle_tesseract(source, licenses, output):
    """按锁定清单复制 Tesseract 程序、依赖 DLL、英文数据与许可文字，逐个核对长度和 SHA-256。"""
    lock = json.loads((ROOT / "workers/color-engine/tesseract-lock.json").read_text(encoding="utf-8"))
    source, licenses = Path(source).absolute(), Path(licenses).absolute()
    for directory in (source, licenses):
        if not directory.is_dir():
            raise ValueError(f"Tesseract 输入目录不存在：{directory}")
    target = output / "tesseract"
    (target / "tessdata").mkdir(parents=True)
    (target / "licenses").mkdir()
    files = []
    for entry in lock["files"]:
        origin = source / entry["path"]
        if not origin.is_file() or origin.is_symlink():
            raise ValueError(f"Tesseract 输入缺少 {entry['path']}")
        if origin.stat().st_size != entry["length"] or digest(origin) != entry["sha256"]:
            raise ValueError(f"Tesseract 输入 {entry['path']} 与锁定版本 {lock['version']} 的长度或 SHA-256 不符")
        shutil.copy2(origin, target / entry["path"])
        files.append({"path": "tesseract/" + entry["path"], "package": entry["package"],
                      "length": entry["length"], "sha256": entry["sha256"]})
    packages = []
    for package in lock["packages"]:
        if package["id"] == "tesseract":
            origin = source / package["licenseInput"]
            if not origin.is_file() or digest(origin) != package["licenseInputSha256"]:
                raise ValueError("Tesseract 许可文字与锁定版本不符")
        else:
            origin = licenses / Path(package["licenseInput"]).name
            if not origin.is_file() or origin.stat().st_size == 0:
                raise ValueError(f"缺少 {package['name']} 的许可文字：{origin.name}")
        license_file = target / "licenses" / f"{package['id']}.txt"
        shutil.copy2(origin, license_file)
        packages.append({key: package[key] for key in ("id", "name", "version", "license", "source", "upstreamLicenseFile")} |
                        {"licenseFile": license_file.relative_to(output).as_posix(), "licenseSha256": digest(license_file),
                         "files": [item["path"] for item in files if item["package"] == package["id"]]})
    version = command([str(target / "tesseract.exe"), "--version"], capture_output=True,
                      cwd=ROOT / "artifacts/tmp", env={"SystemRoot": os.environ["SystemRoot"], "PATH": os.environ["SystemRoot"] + "\\System32"})
    if lock["versionOutput"] not in version.stdout + version.stderr:
        raise ValueError("随包 Tesseract 版本输出与锁定版本不符")
    languages = command([str(target / "tesseract.exe"), "--list-langs", "--tessdata-dir", str(target / "tessdata")], capture_output=True,
                        cwd=ROOT / "artifacts/tmp", env={"SystemRoot": os.environ["SystemRoot"], "PATH": os.environ["SystemRoot"] + "\\System32"})
    if "eng" not in [line.strip() for line in languages.stdout.splitlines()]:
        raise ValueError("随包 Tesseract 无法从组件内加载英文识别数据")
    return {"id": "tesseract", "version": lock["version"], "platform": lock["platform"], "distribution": lock["distribution"],
            "languages": lock["usage"]["languages"], "files": files, "packages": packages}


def build(python, output, tesseract, tesseract_licenses):
    python = shutil.which(python) or python
    probe = command([python, "-I", "-c", "import json,sys,struct; print(json.dumps(dict(base=sys.base_prefix,version=sys.version.split()[0],bits=struct.calcsize('P')*8)))"], capture_output=True)
    environment = json.loads(probe.stdout)
    if os.name != "nt" or environment["bits"] != 64 or not environment["version"].startswith("3.12."):
        raise ValueError("染色发行要求 Windows x64 Python 3.12")
    base = Path(environment["base"])
    output = safe_output(output)
    runtime = output / "python"
    runtime.mkdir(parents=True)
    # 先核对 Tesseract 输入，锁定版本或许可文字不符时不再下载 wheel。
    tesseract_record = bundle_tesseract(tesseract, tesseract_licenses, output)
    for name in ("python.exe", "python3.dll", "python312.dll", "vcruntime140.dll", "vcruntime140_1.dll", "LICENSE.txt"):
        source = base / name
        if not source.is_file():
            raise ValueError(f"Python 运行时缺少 {name}")
        shutil.copy2(source, runtime / name)
    exclusions = shutil.ignore_patterns("__pycache__", "*.pyc", "*.pyo", "site-packages", "test", "tests", "idlelib", "tkinter", "ensurepip")
    shutil.copytree(base / "Lib", runtime / "Lib", ignore=exclusions)
    shutil.copytree(base / "DLLs", runtime / "DLLs", ignore=shutil.ignore_patterns("tcl*", "tk*", "_tkinter.pyd", "*.pdb"))
    shutil.copytree(ROOT / "workers/color-engine/mabitool_color", output / "mabitool_color", ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "*.pyo"))
    shutil.copy2(ROOT / "workers/color-engine/requirements.txt", output / "requirements.txt")
    packages = runtime / "Lib/site-packages"
    packages.mkdir()
    with tempfile.TemporaryDirectory(prefix="color-wheels-", dir=ROOT / "artifacts/tmp") as temporary:
        wheels = Path(temporary)
        command([python, "-I", "-m", "pip", "--isolated", "download", "--only-binary=:all:", "--dest", str(wheels), "-r", str(output / "requirements.txt")])
        wheel_inventory = [{"file": wheel.name, "length": wheel.stat().st_size,
                            "sha256": digest(wheel)}
                           for wheel in sorted(wheels.glob("*.whl"))]
        command([python, "-I", "-m", "pip", "--isolated", "install", "--no-index", "--find-links", str(wheels),
                 "--only-binary=:all:", "--no-compile", "--target", str(packages), "-r", str(output / "requirements.txt")])
    (runtime / "python312._pth").write_text("Lib\nDLLs\nLib/site-packages\n..\nimport site\n", encoding="utf-8")
    inventory = []
    for distribution in sorted(importlib.metadata.distributions(path=[str(packages)]), key=lambda item: item.metadata["Name"].casefold()):
        name = distribution.metadata["Name"]
        if name.lower().startswith(("pyside", "pyqt", "shiboken")):
            raise ValueError("染色数值组件包含 GUI 依赖")
        license_files = []
        for entry in distribution.files or []:
            if any(token in entry.name.lower() for token in ("license", "licence", "copying", "notice", "copyright")):
                source = Path(distribution.locate_file(entry)).resolve()
                if source.is_relative_to(packages.resolve()) and source.is_file():
                    license_files.append(source.relative_to(output).as_posix())
        inventory.append({"name": name, "version": distribution.version,
                          "licenseExpression": distribution.metadata.get("License-Expression"),
                          "license": distribution.metadata.get("License"),
                          "licenseClassifiers": [item for item in distribution.metadata.get_all("Classifier", []) if item.startswith("License ::")],
                          "author": distribution.metadata.get("Author") or distribution.metadata.get("Author-email"),
                          "licenseFiles": sorted(license_files)})
    isolated = command([str(runtime / "python.exe"), "-B", "-c",
                        "import json,sys,numpy,cv2,scipy,PIL,pytesseract,pefile,capstone,mabitool_color.worker; print(json.dumps(dict(prefix=sys.prefix,paths=sys.path,isolated=sys.flags.isolated,numpy=numpy.__version__,opencv=cv2.__version__)))"],
                       cwd=ROOT / "artifacts/tmp", capture_output=True)
    isolation = json.loads(isolated.stdout)
    if not isolation["isolated"] or Path(isolation["prefix"]).resolve() != runtime.resolve():
        raise ValueError("随包 Python 未使用隔离数据路径")
    if any(not Path(path).resolve().is_relative_to(output.resolve()) for path in isolation["paths"]):
        raise ValueError("随包 Python 读取组件外依赖")
    write_json(output / "runtime-build.json", {"schemaVersion": 1, "pythonVersion": environment["version"], "platform": "win-x64",
                                               "wheels": wheel_inventory, "packages": inventory,
                                               "isolatedImport": {"isolated": True, "paths": [Path(path).relative_to(output).as_posix() for path in isolation["paths"]],
                                                                  "numpy": isolation["numpy"], "opencv": isolation["opencv"]},
                                               "tesseract": tesseract_record})
    print(f"已构建隔离染色组件：{output}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--python", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--tesseract-directory", required=True, type=Path,
                        help="与 workers/color-engine/tesseract-lock.json 一致的 Tesseract 安装目录")
    parser.add_argument("--tesseract-licenses", required=True, type=Path,
                        help="随带 DLL 与语言数据的许可文字目录，文件名为 <包 id>.txt")
    arguments = parser.parse_args()
    build(arguments.python, arguments.output, arguments.tesseract_directory, arguments.tesseract_licenses)


if __name__ == "__main__":
    main()
