"""检查复用登记、固定 Git 对象和明确登记的本机工作树快照。"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = Path(__file__).with_name("source-baselines.json")


def safe_path(value):
    return (isinstance(value, str) and bool(value) and "\\" not in value
            and not PurePosixPath(value).is_absolute()
            and all(p not in ("", ".", "..") for p in value.split("/"))
            and ":" not in value and "\0" not in value)


def validate(data, tasks):
    errors = []
    if data.get("schemaVersion") != 1:
        errors.append("不支持的来源清单版本")
    sources = {}
    for source in data["sources"]:
        sid = source["id"]
        if sid in sources:
            errors.append(f"重复来源：{sid}")
        sources[sid] = source
        if not re.fullmatch(r"[0-9a-f]{40}", source["commit"]):
            errors.append(f"{sid}: 提交必须为完整 SHA")
        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", source["repository"]):
            errors.append(f"{sid}: 仓库标识无效")
        if source["licenseStatus"] not in ("MIT", "PolyForm-Noncommercial-1.0.0", "owner-license-pending", "author-granted-MIT"):
            errors.append(f"{sid}: 许可状态无效")
        if source["licenseStatus"] == "author-granted-MIT":
            # 来源作者本人以 MIT 授权并入：须写明作者，并有已登记的 MIT 发行授权。
            if not source.get("authors") or source.get("distributionLicense", {}).get("id") != "MIT":
                errors.append(f"{sid}: 作者授权缺少作者或 MIT 发行授权")
        files = set()
        for entry in source["files"]:
            path = entry["path"]
            if not safe_path(path) or path in files:
                errors.append(f"{sid}: 文件路径无效或重复：{path}")
            files.add(path)
            if not re.fullmatch(r"[0-9a-f]{64}", entry["sha256"]):
                errors.append(f"{sid}/{path}: SHA-256 无效")
        for path in source["licenseEvidence"]:
            if path not in files:
                errors.append(f"{sid}: 许可证据没有固定哈希：{path}")
        if source["licenseStatus"] in ("MIT", "PolyForm-Noncommercial-1.0.0") and not source["licenseEvidence"]:
            errors.append(f"{sid}: 已声明许可缺少证据")
        working_tree = source.get("workingTree")
        if working_tree is not None:
            entries = sorted(source["files"], key=lambda item: item["path"])
            snapshot = hashlib.sha256("".join(f"{item['path']}\0{item['sha256']}\n" for item in entries).encode("utf-8")).hexdigest()
            if (working_tree.get("sha256") != snapshot or not working_tree.get("capturedAt")
                    or any(item.get("contentSource") != "working-tree" for item in entries)):
                errors.append(f"{sid}: 工作树快照登记无效")
        distribution = source.get("distributionLicense")
        if distribution is not None:
            if (distribution.get("id") not in ("MIT", "PolyForm-Noncommercial-1.0.0") or distribution.get("authorizationSource") != "user"
                    or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", distribution.get("authorizationDate", ""))
                    or not distribution.get("copyright") or not safe_path(distribution.get("path", ""))):
                errors.append(f"{sid}: 发行授权登记无效")
            else:
                text = ROOT / distribution["path"]
                if not text.is_file() or hashlib.sha256(text.read_bytes()).hexdigest() != distribution.get("sha256"):
                    errors.append(f"{sid}: 发行许可证据缺失或哈希不符")
        pull_numbers = set()
        for pull in source.get("pullRequests", []):
            number = pull.get("number")
            prefix = f"{sid}/PR{number}"
            if not isinstance(number, int) or number < 1 or number in pull_numbers:
                errors.append(f"{prefix}: PR 编号无效或重复")
            pull_numbers.add(number)
            for field in ("baseCommit", "headCommit", "patchBaseCommit"):
                if not re.fullmatch(r"[0-9a-f]{40}", pull.get(field, "")):
                    errors.append(f"{prefix}: {field} 必须为完整 SHA")
            if "mergeCommit" in pull and not re.fullmatch(r"[0-9a-f]{40}", pull["mergeCommit"]):
                errors.append(f"{prefix}: mergeCommit 必须为完整 SHA")
            commits = pull.get("commits", [])
            if (not commits or any(not re.fullmatch(r"[0-9a-f]{40}", c) for c in commits)
                    or commits[-1] != pull.get("headCommit")):
                errors.append(f"{prefix}: 补丁提交链无效")
            if not pull.get("authors") or any(not isinstance(a, str) or not a.strip() for a in pull["authors"]):
                errors.append(f"{prefix}: 缺少作者")
            if (pull.get("disposition") not in ("platform-excluded", "included-in-main", "rejected",
                                                "dependency-reference", "adopted-function")
                    or not isinstance(pull.get("includedInMain"), bool)
                    or pull["includedInMain"] != (pull["disposition"] == "included-in-main")
                    or not pull.get("purpose", "").strip()):
                errors.append(f"{prefix}: 缺少明确的采纳结果")
            paths = pull.get("changedFiles", [])
            if not paths or len(set(paths)) != len(paths) or any(not safe_path(p) for p in paths):
                errors.append(f"{prefix}: 改动路径无效")
            selected = pull.get("selectedFiles", [])
            if len({f["path"] for f in selected}) != len(selected):
                errors.append(f"{prefix}: 重复采纳文件")
            for entry in selected:
                if entry["path"] not in paths or not re.fullmatch(r"[0-9a-f]{64}", entry.get("sha256", "")):
                    errors.append(f"{prefix}: 采纳文件缺少固定哈希")
            if pull.get("disposition") == "adopted-function" and not any(f.get("functions") for f in selected):
                errors.append(f"{prefix}: 未指定采纳函数")
            if any(not re.fullmatch(r"[A-Za-z0-9_.-]+==[^\s]+", d) for d in pull.get("dependencies", [])):
                errors.append(f"{prefix}: 依赖缺少精确版本")
    ids = set()
    for feature in data["features"]:
        fid = feature["id"]
        if fid in ids:
            errors.append(f"重复功能：{fid}")
        ids.add(fid)
        source = sources.get(feature["source"])
        files = {f["path"] for f in source["files"]} if source else set()
        if not source or not feature["entries"] or any(p not in files for p in feature["entries"]):
            errors.append(f"{fid}: 功能入口缺少来源或哈希")
        if tasks is not None and feature["task"] not in tasks:
            errors.append(f"{fid}: 未知实施任务 {feature['task']}")
        if not feature["acceptance"].strip() or not feature["module"].strip():
            errors.append(f"{fid}: 缺少模块或验收场景")
        if feature["migrationStatus"] not in ("mapped", "ported"):
            errors.append(f"{fid}: 无效迁移状态")
    if not sources or not ids:
        errors.append("来源和功能清单不能为空")
    return errors


def load_tasks(root):
    """读取实施任务编号；仓库没有任务清单（例如公开导出树）时返回 None，跳过任务登记检查。"""
    changes = root / "openspec/changes"
    if not changes.is_dir():
        return None
    tasks = set()
    for task_file in changes.glob("*/tasks.md"):
        numbers = re.findall(r"^- \[[ x]\] (\d+\.\d+) ", task_file.read_text(encoding="utf-8"), re.M)
        if task_file.parent.name == "unified-modular-backend":
            tasks.update(numbers)
        tasks.update(f"{task_file.parent.name}:{number}" for number in numbers)
    return tasks


def git(root, *args):
    return subprocess.run(["git", "-C", str(root), *args], check=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30).stdout


def verify_source(source, root):
    errors = []
    sid, commit = source["id"], source["commit"]
    try:
        if git(root, "cat-file", "-t", commit).strip() != b"commit":
            return [f"{sid}: 对象不是提交"]
        names = git(root, "ls-tree", "-r", "--name-only", "-z", commit).split(b"\0")
        if (b"LICENSE" in names) != source["rootLicensePresent"]:
            errors.append(f"{sid}: 根 LICENSE 登记不符")
        for entry in source["files"]:
            if entry.get("contentSource") == "working-tree":
                if not source.get("workingTree") or not safe_path(entry["path"]):
                    errors.append(f"{sid}/{entry['path']}: 工作树来源缺少快照")
                    continue
                blob = (root / entry["path"]).read_bytes()
            else:
                blob = git(root, "show", f"{commit}:{entry['path']}")
            if hashlib.sha256(blob).hexdigest() != entry["sha256"]:
                errors.append(f"{sid}/{entry['path']}: 固定源码哈希不符")
        for pull in source.get("pullRequests", []):
            prefix = f"{sid}/PR{pull['number']}"
            for field in ("baseCommit", "headCommit", "patchBaseCommit"):
                if git(root, "cat-file", "-t", pull[field]).strip() != b"commit":
                    errors.append(f"{prefix}: {field} 对象不是提交")
            included = git(root, "merge-base", pull["headCommit"], commit).strip().decode() == pull["headCommit"]
            if included != pull["includedInMain"]:
                errors.append(f"{prefix}: 主线祖先关系不符")
            if "mergeCommit" in pull:
                parents = git(root, "log", "-1", "--format=%P", pull["mergeCommit"]).decode().split()
                if parents != [pull["baseCommit"], pull["headCommit"]]:
                    errors.append(f"{prefix}: 合并 base/head 证据不符")
            for entry in pull.get("selectedFiles", []):
                blob = git(root, "show", f"{pull['headCommit']}:{entry['path']}")
                if hashlib.sha256(blob).hexdigest() != entry["sha256"]:
                    errors.append(f"{prefix}/{entry['path']}: 采纳源码哈希不符")
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired, OSError):
        errors.append(f"{sid}: 无法读取指定仓库的固定 Git 对象")
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", action="append", default=[], metavar="ID=CHECKOUT",
                        help="验证指定来源的固定提交和文件；不写仓库或读取游戏")
    args = parser.parse_args()
    data = json.loads(MANIFEST.read_text(encoding="utf-8"))
    errors = validate(data, load_tasks(ROOT))
    selected = set()
    for mapping in args.source:
        sid, separator, directory = mapping.partition("=")
        source = next((s for s in data["sources"] if s["id"] == sid), None)
        if not separator or not directory or not source or sid in selected:
            errors.append(f"来源参数无效或重复：{sid}")
        elif not errors:
            selected.add(sid)
            errors.extend(verify_source(source, Path(directory)))
    for error in errors:
        print(error)
    print(f"来源登记：{len(data['sources'])} 来源，{len(data['features'])} 功能；"
          f"Git 对象核对 {len(selected)} 来源；{len(errors)} 错误")
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
