"""从固定依赖构建可搬移的隔离 Python 染色组件。"""
import argparse
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]

# 瘦身规则：路径相对组件根，按 glob 匹配，命中的文件或目录整体删除。每条都以 Worker 的实际导入闭包为依据
# （IMPORT_CLOSURE_PROBE 导入 Worker 入口、全部模块并执行函数内的延迟导入），删除后由同一探针复核。
SLIM_RULES = (
    ("python/DLLs/_ssl.pyd", "TLS 扩展；Worker 不联网，导入闭包不含 ssl"),
    ("python/DLLs/libssl-3.dll", "只被 _ssl 引用"),
    ("python/DLLs/_sqlite3.pyd", "SQLite 扩展；Worker 不读写数据库"),
    ("python/DLLs/sqlite3.dll", "只被 _sqlite3 引用"),
    ("python/Lib/sqlite3", "sqlite3 标准库包，缺少扩展后无法导入"),
    ("python/DLLs/_test*.pyd", "CPython 自测扩展"),
    ("python/DLLs/_ctypes_test.pyd", "CPython 自测扩展"),
    ("python/Lib/site-packages/cv2/opencv_videoio_ffmpeg*.dll", "视频读写插件；Worker 只用 imgproc 处理内存中的截图"),
    ("python/Lib/site-packages/cv2/data", "Haar 级联模型；Worker 不做级联检测，cv2 只在目录存在时把它当子模块导入"),
    *((f"python/Lib/site-packages/scipy/{name}", "SciPy 子包；Worker 只用 spatial、optimize 及其依赖")
      for name in ("cluster", "datasets", "differentiate", "fftpack", "integrate", "interpolate", "io", "misc", "ndimage",
                   "odr", "signal", "stats")),
    ("python/Lib/site-packages/scipy/conftest.py", "SciPy 测试配置"),
    ("python/Lib/site-packages/scipy/**/tests", "SciPy 自带测试"),
    ("python/Lib/site-packages/numpy/**/tests", "NumPy 自带测试"),
    *((f"python/Lib/site-packages/PIL/{name}.cp312-win_amd64.pyd", "Pillow 编解码与附加扩展；Worker 只读写 PNG 并做 RGB 转换")
      for name in ("_avif", "_webp", "_imagingft", "_imagingcms", "_imagingtk", "_imagingmorph")),
    ("python/Lib/site-packages/capstone/include", "Capstone C 头文件"),
    ("python/Lib/site-packages/bin", "pip 生成的命令行入口（f2py、numpy-config、pytesseract）"),
    ("python/Lib/site-packages/*.whl", "安装过程残留的 wheel 文件"),
    ("**/*.pdb", "调试符号"),
)

# 在随包解释器内运行：导入 Worker 入口与全部模块，并执行源码中位于函数内的第三方导入及其实际调用方式，
# 任何依赖被瘦身删掉都会在这里以 ImportError 或调用失败暴露。
IMPORT_CLOSURE_PROBE = """
import importlib, io, json, pkgutil, sys
import numpy as np
import mabitool_color, mabitool_color.worker
for info in pkgutil.walk_packages(mabitool_color.__path__, 'mabitool_color.'):
    importlib.import_module(info.name)
from scipy.optimize import least_squares
from scipy.spatial import cKDTree
from scipy.spatial.distance import cdist
import capstone, cv2, pefile, pytesseract
from PIL import Image, ImageDraw
least_squares(lambda p: p - 1., np.zeros(2), bounds=(-2., 2.), loss='soft_l1', f_scale=8., max_nfev=5, diff_step=.002)
cKDTree(np.random.default_rng(0).random((8, 2)), boxsize=1.).query(np.zeros((2, 2)))
cdist(np.ones((1, 4)), np.zeros((3, 4)), metric='cityblock')
capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
image = np.zeros((32, 32, 3), np.uint8)
cv2.connectedComponentsWithStats(cv2.cvtColor(image, cv2.COLOR_RGB2GRAY))
buffer = io.BytesIO(); Image.fromarray(image).save(buffer, format='PNG')
Image.open(io.BytesIO(buffer.getvalue())).convert('RGB')
print(json.dumps(dict(prefix=sys.prefix, paths=sys.path, isolated=sys.flags.isolated, numpy=np.__version__,
                      opencv=cv2.__version__, modules=len(sys.modules))))
"""

# 截图读字的 Tesseract 调用形状：同一组 PNG 分别交给原始与去调试信息的程序，输出须逐字节一致。
OCR_EQUIVALENCE_PROBE = """
import io, json, subprocess, sys
import cv2, numpy as np
from PIL import Image
original, stripped, tessdata = sys.argv[1:4]
cases = []
for text in ('#0A1B2C', '#7F7F7F', '#F9F6F6', '#E06C3D', '#123456', '#ABCDEF', '87', '120', '5'):
    for scale, thickness, threshold in ((1.0, 2, None), (1.6, 3, 170), (0.7, 1, 200)):
        image = np.full((64, 360, 3), 255, np.uint8)
        cv2.putText(image, text, (10, 46), cv2.FONT_HERSHEY_SIMPLEX, scale, (40, 40, 40), thickness, cv2.LINE_AA)
        if threshold is not None:
            gray = cv2.cvtColor(image, cv2.COLOR_RGB2GRAY); image = np.uint8(gray >= threshold) * 255
        buffer = io.BytesIO(); Image.fromarray(image).save(buffer, format='PNG')
        whitelist = '0123456789' if not text.startswith('#') else '#0123456789ABCDEF'
        for psm in (7, 8):
            cases.append((buffer.getvalue(), ['stdin', 'stdout', '-l', 'eng', '--tessdata-dir', tessdata, '--psm', str(psm),
                                              '-c', 'tessedit_char_whitelist=' + whitelist]))
outputs = []
for data, arguments in cases:
    pair = [subprocess.run([program, *arguments], input=data, capture_output=True, timeout=30,
                           creationflags=subprocess.CREATE_NO_WINDOW) for program in (original, stripped)]
    if pair[0].returncode or pair[1].returncode or pair[0].stdout != pair[1].stdout:
        raise SystemExit('Tesseract output differs: ' + ' '.join(arguments[5:]))
    outputs.append(pair[0].stdout.decode('utf-8', 'replace').strip())
print(json.dumps(dict(cases=len(cases), nonEmpty=sum(1 for item in outputs if item))))
"""


def command(arguments, **kwargs):
    return subprocess.run(arguments, check=True, text=True, **kwargs)


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def minimal_environment():
    return {"SystemRoot": os.environ["SystemRoot"], "PATH": os.environ["SystemRoot"] + "\\System32"}


def safe_output(path):
    path = path.absolute()
    allowed = (ROOT / "artifacts").resolve()
    if not path.is_relative_to(allowed) or path.exists():
        raise ValueError("组件输出须为 artifacts 内的新目录")
    for parent in (path, *path.parents):
        if parent.exists() and (parent.is_symlink() or parent.is_junction()):
            raise ValueError("组件输出包含链接目录")
    return path


def strip_pe_debug(data):
    """移除 MinGW 构建的 PE 文件末尾的 DWARF `.debug_*` 节，同时去掉 COFF 符号表、失效的 Authenticode 签名并修正头部。

    只处理调试节连续位于节表末尾、其原始数据位于全部保留节之后的布局；其他布局报错，不做猜测性改写。
    """
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("不是 PE 文件")
    header = pe + 4
    count = struct.unpack_from("<H", data, header + 2)[0]
    symbols, symbol_count = struct.unpack_from("<II", data, header + 8)
    optional_size = struct.unpack_from("<H", data, header + 16)[0]
    optional = header + 20
    directories = optional + {0x10B: 96, 0x20B: 112}[struct.unpack_from("<H", data, optional)[0]]
    directory_count = struct.unpack_from("<I", data, directories - 4)[0]
    alignment = struct.unpack_from("<I", data, optional + 32)[0]
    strings = symbols + symbol_count * 18
    sections = []
    for index in range(count):
        offset = optional + optional_size + index * 40
        raw_name = data[offset:offset + 8].rstrip(b"\0")
        name = raw_name
        if raw_name.startswith(b"/"):
            if not symbols:
                raise ValueError("长节名缺少字符串表")
            start = strings + int(raw_name[1:])
            name = data[start:data.index(b"\0", start)]
        virtual_size, address, raw_size, raw_pointer = struct.unpack_from("<IIII", data, offset + 8)
        sections.append(dict(offset=offset, raw_name=raw_name, name=name, virtual_size=virtual_size, address=address,
                             raw_size=raw_size, raw_pointer=raw_pointer))
    debug = [index for index, section in enumerate(sections) if section["name"].startswith(b".debug_")]
    if not debug:
        return data
    kept = sections[:debug[0]]
    if debug != list(range(debug[0], count)) or any(section["raw_name"].startswith(b"/") for section in kept):
        raise ValueError("调试节不在节表末尾，或保留节使用了长节名")
    cut = min(sections[index]["raw_pointer"] for index in debug)
    if max(section["raw_pointer"] + section["raw_size"] for section in kept) > cut or (symbols and symbols < cut):
        raise ValueError("调试节之后还有保留数据")
    image_end = max(section["address"] + max(section["virtual_size"], section["raw_size"]) for section in kept)
    for index in range(directory_count):
        address, size = struct.unpack_from("<II", data, directories + index * 8)
        if index == 4:
            if address and address < cut:
                raise ValueError("签名数据位于保留区域内")
        elif address and address + size > image_end:
            raise ValueError(f"数据目录 {index} 指向被移除的节")
    output = bytearray(data[:cut])
    struct.pack_into("<HxxxxII", output, header + 2, len(kept), 0, 0)
    for index in debug:
        output[sections[index]["offset"]:sections[index]["offset"] + 40] = bytes(40)
    struct.pack_into("<I", output, optional + 56, (image_end + alignment - 1) // alignment * alignment)
    struct.pack_into("<II", output, directories + 4 * 8, 0, 0)
    struct.pack_into("<I", output, optional + 64, 0)
    # PE 校验和：16 位字带进位累加（奇数长度补零），再加文件长度。
    total = 0
    padded = bytes(output) + b"\0" * (len(output) % 2)
    for (word,) in struct.iter_unpack("<H", padded):
        total += word
        total = (total & 0xFFFF) + (total >> 16)
    struct.pack_into("<I", output, optional + 64, ((total + (total >> 16)) & 0xFFFF) + len(output))
    return bytes(output)


def bundle_tesseract(source, licenses, output):
    """按锁定清单复制 Tesseract 程序、依赖 DLL、英文数据与许可文字，逐个核对长度和 SHA-256；
    标记 stripDebug 的文件去掉调试节后再核对一次处理后的长度和 SHA-256，保证可复现。"""
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
        record = {"path": "tesseract/" + entry["path"], "package": entry["package"],
                  "length": entry["length"], "sha256": entry["sha256"]}
        if entry.get("stripDebug"):
            stripped = strip_pe_debug(origin.read_bytes())
            if len(stripped) != entry["strippedLength"] or hashlib.sha256(stripped).hexdigest() != entry["strippedSha256"]:
                raise ValueError(f"Tesseract {entry['path']} 去调试信息后的长度或 SHA-256 与锁定值不符")
            (target / entry["path"]).write_bytes(stripped)
            record |= {"length": len(stripped), "sha256": entry["strippedSha256"],
                       "sourceLength": entry["length"], "sourceSha256": entry["sha256"], "debugSectionsRemoved": True}
        else:
            shutil.copy2(origin, target / entry["path"])
        files.append(record)
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
                      cwd=ROOT / "artifacts/tmp", env=minimal_environment())
    if lock["versionOutput"] not in version.stdout + version.stderr:
        raise ValueError("随包 Tesseract 版本输出与锁定版本不符")
    languages = command([str(target / "tesseract.exe"), "--list-langs", "--tessdata-dir", str(target / "tessdata")], capture_output=True,
                        cwd=ROOT / "artifacts/tmp", env=minimal_environment())
    if "eng" not in [line.strip() for line in languages.stdout.splitlines()]:
        raise ValueError("随包 Tesseract 无法从组件内加载英文识别数据")
    return {"id": "tesseract", "version": lock["version"], "platform": lock["platform"], "distribution": lock["distribution"],
            "languages": lock["usage"]["languages"], "files": files, "packages": packages}


def verify_ocr_equivalence(runtime, source, output):
    """以随包解释器生成 HEX 与倒计时样张，对比原始与去调试信息的 Tesseract 输出。"""
    result = command([str(runtime / "python.exe"), "-B", "-c", OCR_EQUIVALENCE_PROBE, str(Path(source).absolute() / "tesseract.exe"),
                      str(output / "tesseract/tesseract.exe"), str(output / "tesseract/tessdata")],
                     cwd=ROOT / "artifacts/tmp", capture_output=True, env=minimal_environment())
    return json.loads(result.stdout)


def slim_component(output):
    """按 SLIM_RULES 删除组件内不在导入闭包中的文件，返回每条规则删除的文件数与字节数，以及整体删除的目录。"""
    removed, directories = [], []
    for pattern, reason in SLIM_RULES:
        files = size = 0
        for match in sorted(output.glob(pattern), key=lambda item: len(item.parts), reverse=True):
            if not match.exists():
                continue
            if match.is_symlink() or match.is_junction() or not match.resolve().is_relative_to(output.resolve()):
                raise ValueError(f"瘦身目标越界或为链接：{match}")
            entries = [match] if match.is_file() else [item for item in match.rglob("*") if item.is_file()]
            files += len(entries)
            size += sum(item.stat().st_size for item in entries)
            if match.is_file():
                match.unlink()
            else:
                directories.append(match.relative_to(output).as_posix() + "/")
                shutil.rmtree(match)
        removed.append({"pattern": pattern, "reason": reason, "files": files, "bytes": size})
    return removed, directories


def verify_import_closure(runtime, output):
    """启动随包解释器跑导入闭包探针，失败即中止打包；同时确认解释器只读组件内路径。"""
    result = subprocess.run([str(runtime / "python.exe"), "-B", "-c", IMPORT_CLOSURE_PROBE], cwd=ROOT / "artifacts/tmp",
                            capture_output=True, text=True, env=minimal_environment())
    if result.returncode:
        raise ValueError("瘦身后的染色组件导入闭包自检失败：\n" + result.stderr[-4000:])
    isolation = json.loads(result.stdout.splitlines()[-1])
    if not isolation["isolated"] or Path(isolation["prefix"]).resolve() != runtime.resolve():
        raise ValueError("随包 Python 未使用隔离数据路径")
    if any(not Path(path).resolve().is_relative_to(output.resolve()) for path in isolation["paths"]):
        raise ValueError("随包 Python 读取组件外依赖")
    return isolation


def copy_python_runtime(base, output):
    runtime = output / "python"
    runtime.mkdir(parents=True)
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
    (runtime / "python312._pth").write_text("Lib\nDLLs\nLib/site-packages\n..\nimport site\n", encoding="utf-8")
    return runtime


def install_packages(python, output):
    packages = output / "python/Lib/site-packages"
    packages.mkdir()
    with tempfile.TemporaryDirectory(prefix="color-wheels-", dir=ROOT / "artifacts/tmp") as temporary:
        wheels = Path(temporary)
        command([python, "-I", "-m", "pip", "--isolated", "download", "--only-binary=:all:", "--dest", str(wheels), "-r", str(output / "requirements.txt")])
        wheel_inventory = [{"file": wheel.name, "length": wheel.stat().st_size,
                            "sha256": digest(wheel)}
                           for wheel in sorted(wheels.glob("*.whl"))]
        command([python, "-I", "-m", "pip", "--isolated", "install", "--no-index", "--find-links", str(wheels),
                 "--only-binary=:all:", "--no-compile", "--target", str(packages), "-r", str(output / "requirements.txt")])
    return wheel_inventory


def package_inventory(output):
    packages = output / "python/Lib/site-packages"
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
    return inventory


def finish(output, runtime, python_version, wheel_inventory, tesseract_record, tesseract_source):
    """瘦身、复核许可文字与导入闭包、核对 OCR 等价，再写出构建记录。"""
    licenses = {path for distribution in package_inventory(output) for path in distribution["licenseFiles"]}
    slimming, directories = slim_component(output)
    # 随整个子包一起删除的许可文字只覆盖被删代码；其余许可文字必须保留。
    missing = sorted(path for path in licenses if not (output / path).is_file() and not path.startswith(tuple(directories)))
    if missing:
        raise ValueError("瘦身规则删除了许可文字：" + "、".join(missing))
    isolation = verify_import_closure(runtime, output)
    ocr = verify_ocr_equivalence(runtime, tesseract_source, output)
    write_json(output / "runtime-build.json", {"schemaVersion": 1, "pythonVersion": python_version, "platform": "win-x64",
                                               "wheels": wheel_inventory, "packages": package_inventory(output),
                                               "slimming": slimming,
                                               "isolatedImport": {"isolated": True, "paths": [Path(path).relative_to(output).as_posix() for path in isolation["paths"]],
                                                                  "numpy": isolation["numpy"], "opencv": isolation["opencv"],
                                                                  "closureModules": isolation["modules"]},
                                               "tesseract": tesseract_record | {"ocrEquivalence": ocr}})


def build(python, output, tesseract, tesseract_licenses):
    python = shutil.which(python) or python
    probe = command([python, "-I", "-c", "import json,sys,struct; print(json.dumps(dict(base=sys.base_prefix,version=sys.version.split()[0],bits=struct.calcsize('P')*8)))"], capture_output=True)
    environment = json.loads(probe.stdout)
    if os.name != "nt" or environment["bits"] != 64 or not environment["version"].startswith("3.12."):
        raise ValueError("染色发行要求 Windows x64 Python 3.12")
    output = safe_output(output)
    output.mkdir(parents=True)
    # 先核对 Tesseract 输入，锁定版本或许可文字不符时不再下载 wheel。
    tesseract_record = bundle_tesseract(tesseract, tesseract_licenses, output)
    runtime = copy_python_runtime(Path(environment["base"]), output)
    wheel_inventory = install_packages(python, output)
    finish(output, runtime, environment["version"], wheel_inventory, tesseract_record, tesseract)
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
