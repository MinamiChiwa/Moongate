"""按 repository-rules.json 的 artifactPolicy 回收生成物；默认预览，--apply 才删除。

--target 指定目录时改为移交模式：把登记生成目录下的指定目录移到待清理区（打包脚本使用）。
"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import time
from uuid import uuid4

from repository_paths import ROOT, check_path, is_link, load_rules

REFERENCE_SUFFIXES = {".md", ".json", ".yaml", ".yml", ".txt", ".csv", ".cs", ".ts", ".mjs", ".js"}
REFERENCE_MAX_BYTES = 4 * 1024 * 1024
REFERENCE_STOP = "*?<{$"


def raise_walk_error(error):
    raise error


# ---------------------------------------------------------------- 移交模式（--target）

def check_tree(target):
    if not target.is_dir():
        raise ValueError(f"生成目录被文件占用：{target}")
    for current, directories, files in os.walk(target, followlinks=False, onerror=raise_walk_error):
        for name in directories + files:
            path = Path(current) / name
            if is_link(path):
                raise ValueError(f"生成目录内包含联接或符号链接：{path}")


def check_identity(root):
    if not (root / "MabiTool.slnx").is_file() or not (root / "global.json").is_file():
        raise ValueError("缺少仓库标识，拒绝清理")


def plan(root, rules, selected):
    check_identity(root)
    allowed = [check_path(root, relative) for relative in rules["cleanTargets"]]
    queue = check_path(root, rules["cleanupQueue"])
    if any(queue == target or queue.is_relative_to(target) or target.is_relative_to(queue) for target in allowed):
        raise ValueError("待清理区与生成目录范围重叠")
    targets = []
    for relative in selected:
        target = check_path(root, relative)
        if not any(target == parent or target.is_relative_to(parent) for parent in allowed):
            raise ValueError(f"目录未登记为生成目录：{relative}")
        if any(target == previous or target.is_relative_to(previous) or previous.is_relative_to(target)
               for _, previous in targets):
            raise ValueError(f"移交目标重复或相互包含：{relative}")
        if not target.exists():
            continue
        check_tree(target)
        targets.append((target.relative_to(root).as_posix(), target))
    return targets


def write_manifest(path, manifest):
    temporary = path.with_suffix(".json.tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as output:
        json.dump(manifest, output, ensure_ascii=False, indent=2)
        output.write("\n")
        output.flush()
        os.fsync(output.fileno())
    os.replace(temporary, path)


def handoff(root, rules, selected, apply=False):
    root = Path(root).absolute()
    targets = plan(root, rules, selected)
    result = {"mode": "apply" if apply else "dry-run", "targets": [r for r, _ in targets],
              "cleanupQueue": rules["cleanupQueue"], "batch": None}
    if not apply or not targets:
        return result
    # 整批复查后才创建批次；同卷重命名保留内容，移动失败不改用复制或删除。
    targets = plan(root, rules, selected)
    batch_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid4().hex
    batch_relative = f'{rules["cleanupQueue"]}/{batch_id}'
    batch = check_path(root, batch_relative)
    batch.mkdir(parents=True, exist_ok=False)
    check_path(root, batch_relative)
    manifest_path = batch / "manifest.json"
    manifest = {"schemaVersion": 1, "createdAtUtc": datetime.now(timezone.utc).isoformat(),
                "sourceRoot": str(root), "state": "pending", "targets": [
                    {"source": relative, "destination": f"{batch_relative}/{relative}", "state": "pending"}
                    for relative, _ in targets]}
    write_manifest(manifest_path, manifest)
    for entry in manifest["targets"]:
        try:
            target = check_path(root, entry["source"])
            check_tree(target)
            destination = check_path(root, entry["destination"])
            destination.parent.mkdir(parents=True, exist_ok=True)
            check_path(root, entry["destination"])
            if destination.exists():
                raise ValueError(f"待清理目标已存在：{entry['destination']}")
            os.rename(target, destination)
        except (ValueError, OSError) as error:
            entry["state"] = "failed"
            entry["error"] = str(error)
            manifest["state"] = "partial"
            write_manifest(manifest_path, manifest)
            raise
        entry["state"] = "moved"
        write_manifest(manifest_path, manifest)
    manifest["state"] = "complete"
    write_manifest(manifest_path, manifest)
    result["batch"] = batch_relative
    return result


# ---------------------------------------------------------------- 策略回收模式（默认）

def entry_is_link(entry):
    if entry.is_symlink():
        return True
    try:
        attributes = getattr(entry.stat(follow_symlinks=False), "st_file_attributes", 0)
    except OSError:
        return True
    return bool(attributes & 0x400)


def touched_at(metadata):
    # 复制保留原修改时间，Windows 上同时看创建时间，避免删掉刚复制出的工作区。
    created = getattr(metadata, "st_birthtime", None)
    if created is None and os.name == "nt":
        created = metadata.st_ctime
    return max(metadata.st_mtime, created or 0)


class Area:
    def __init__(self, relative, category):
        self.relative = relative
        self.category = category
        self.files = self.bytes = 0
        self.kept_files = 0
        self.recent = self.links = self.failed = 0
        self.delete = []          # (path, size)
        self.directories = []     # 目录在扫描时的时间，仅删除已空且不在保护期内的目录
        self.targets = {}         # 区域下一级目录 -> [文件数, 字节]
        self.deleted_files = self.deleted_bytes = self.removed_directories = 0

    def mark(self, path, size, area_root):
        self.delete.append((path, size))
        parts = Path(path).relative_to(area_root).parts
        key = f"{self.relative}/{parts[0]}" if len(parts) > 1 else self.relative
        bucket = self.targets.setdefault(key, [0, 0])
        bucket[0] += 1
        bucket[1] += size

    def summary(self, apply):
        planned = len(self.delete)
        planned_bytes = sum(size for _, size in self.delete)
        result = {"category": self.category, "files": self.files, "bytes": self.bytes,
                  "deleteFiles": planned, "deleteBytes": planned_bytes,
                  "keptFiles": self.kept_files, "skippedRecent": self.recent, "skippedLinks": self.links,
                  "targets": [{"path": key, "files": value[0], "bytes": value[1]}
                              for key, value in sorted(self.targets.items())]}
        if apply:
            result.update(deletedFiles=self.deleted_files, deletedBytes=self.deleted_bytes,
                          removedDirectories=self.removed_directories, failed=self.failed,
                          remainingFiles=self.files - self.deleted_files)
        else:
            result["remainingFiles"] = self.files - planned
        return result


def walk(directory, visit, area, depth=0):
    """不跟随链接遍历；visit(path, entry, metadata, context) 对文件返回是否删除。"""
    try:
        with os.scandir(directory) as iterator:
            entries = list(iterator)
    except OSError:
        area.failed += 1
        return
    for entry in entries:
        if entry_is_link(entry):
            area.links += 1
            continue
        if entry.is_dir(follow_symlinks=False):
            try:
                area.directories.append((entry.path, touched_at(entry.stat(follow_symlinks=False)), depth + 1))
            except OSError:
                pass
            walk(entry.path, visit, area, depth + 1)
            continue
        try:
            metadata = entry.stat(follow_symlinks=False)
        except OSError:
            area.failed += 1
            continue
        area.files += 1
        area.bytes += metadata.st_size
        visit(entry, metadata)


def scan_area(root, relative, category, now, protect, decide):
    area = Area(relative, category)
    try:
        area_root = check_path(root, relative)
    except ValueError:
        area.links += 1
        return area
    if not area_root.is_dir():
        return area

    def visit(entry, metadata):
        if not decide(entry.path, metadata):
            area.kept_files += 1
        elif now - touched_at(metadata) < protect:
            area.recent += 1
        else:
            area.mark(entry.path, metadata.st_size, area_root)

    walk(str(area_root), visit, area)
    area.root = area_root
    return area


def reference_paths(root, sources, prefixes):
    pattern = re.compile(r"artifacts[/\\](?:" + "|".join(re.escape(p) for p in prefixes)
                         + r")(?:[/\\][^\s)\]}\"'`|，。；、：！？（）【】]*)?")
    exact, partial = set(), set()
    for source in sources:
        path = root / source
        files = [path] if path.is_file() else ([Path(c) / n for c, _, names in os.walk(path) for n in names]
                                               if path.is_dir() else [])
        for file in files:
            if file.suffix.lower() not in REFERENCE_SUFFIXES or is_link(file):
                continue
            try:
                if file.stat().st_size > REFERENCE_MAX_BYTES:
                    continue
                text = file.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            for match in pattern.finditer(text):
                value = match.group(0).replace("\\", "/").split("#", 1)[0]
                cut = min((value.find(c) for c in REFERENCE_STOP if c in value), default=-1)
                if cut >= 0:
                    # 只到区域根的通配引用（如 artifacts/test-results/<runId>）不指向具体证据。
                    if len([p for p in value[:cut].split("/") if p]) >= 3:
                        partial.add(value[:cut])
                    continue
                exact.add(value.rstrip(".,:;/"))
    return exact, partial


def evidence_decider(root, policy, exact, partial):
    extensions = {e.lower() for e in policy["evidenceExtensions"]}
    payload_segments = {s.lower() for s in policy["payloadSegments"]}
    payload_extensions = {e.lower() for e in policy["payloadExtensions"]}
    payload_dirs = {}

    def payload(directory):
        # 目录直接含可执行文件，或路径段属于发行包/构建/安装副本，即视为随附载荷。
        if directory not in payload_dirs:
            try:
                names = os.listdir(directory)
            except OSError:
                names = []
            payload_dirs[directory] = any(os.path.splitext(n)[1].lower() in payload_extensions for n in names)
        return payload_dirs[directory]

    def delete(path, _metadata):
        relative = Path(path).relative_to(root).as_posix()
        if relative in exact:
            return False
        parts = relative.split("/")
        referenced = any("/".join(parts[:i]) in exact for i in range(3, len(parts))) or \
            any(relative.startswith(prefix) for prefix in partial)
        if not referenced or os.path.splitext(path)[1].lower() not in extensions:
            return True
        if any(p.lower() in payload_segments for p in parts[2:-1]):
            return True
        current = Path(path).parent
        while len(current.relative_to(root).parts) >= 3:
            if payload(str(current)):
                return True
            current = current.parent
        return False

    return delete


def list_worktrees(root):
    try:
        output = subprocess.run(["git", "-C", str(root), "worktree", "list", "--porcelain"],
                                capture_output=True, text=True, encoding="utf-8", timeout=20, check=True).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    worktrees, current = {}, None
    for line in output.splitlines():
        if line.startswith("worktree "):
            current = os.path.normcase(os.path.abspath(line[9:]))
            worktrees[current] = {"locked": False}
        elif current and (line == "locked" or line.startswith("locked ")):
            worktrees[current]["locked"] = True
    return worktrees


def scan_worktrees(root, policy, now, protect):
    base = root / policy["worktrees"]
    result = []
    if not base.is_dir() or is_link(base):
        return result, None
    registered = list_worktrees(root)
    agent_pattern = re.compile(policy["agentWorktreePattern"])
    for child in sorted(base.iterdir()):
        relative = child.relative_to(root).as_posix()
        if is_link(child) or not child.is_dir():
            result.append({"path": relative, "state": "skipped-link"})
            continue
        info = None if registered is None else registered.get(os.path.normcase(os.path.abspath(child)))
        if registered is None:
            result.append({"path": relative, "state": "unknown-git-unavailable"})
            continue
        if info is None:
            area = scan_area(root, relative, "worktree-residual", now, protect, lambda p, m: True)
            result.append({"path": relative, "state": "unregistered", "area": area})
            continue
        if not agent_pattern.match(child.name):
            # 其他会话或用户手建的工作树不归本工具链管理，只列出。
            result.append({"path": relative, "state": "foreign"})
            continue
        if info["locked"]:
            result.append({"path": relative, "state": "active-locked"})
            continue
        area = scan_area(root, relative, "worktree-stale", now, protect, lambda p, m: True)
        state = "active-recent" if area.recent or area.links else "stale"
        result.append({"path": relative, "state": state, "files": area.files})
    return result, registered


def remove_file(root_real, path, size, area, now, protect):
    try:
        metadata = os.lstat(path)
        if stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & 0x400:
            area.links += 1
            return
        if not os.path.realpath(os.path.dirname(path)).startswith(root_real + os.sep):
            area.failed += 1
            return
        if now - touched_at(metadata) < protect:
            area.recent += 1
            return
        try:
            os.unlink(path)
        except PermissionError:
            if metadata.st_mode & stat.S_IWRITE:
                raise
            os.chmod(path, stat.S_IWRITE)
            os.unlink(path)
        area.deleted_files += 1
        area.deleted_bytes += size
    except FileNotFoundError:
        pass
    except OSError:
        area.failed += 1


def apply_area(root_real, area, now, protect):
    for path, size in area.delete:
        remove_file(root_real, path, size, area, now, protect)
    for path, touched, _depth in sorted(area.directories, key=lambda item: -item[2]):
        if now - touched < protect:
            continue
        try:
            if not os.path.realpath(path).startswith(root_real + os.sep):
                continue
            os.rmdir(path)
            area.removed_directories += 1
        except OSError:
            continue
    if area.category == "worktree-residual" and getattr(area, "root", None) is not None:
        try:
            os.rmdir(area.root)
        except OSError:
            pass


def sweep(root, rules, apply=False, protect_hours=None, now=None):
    root = Path(root).absolute()
    check_identity(root)
    check_path(root, "artifacts")
    policy = rules["artifactPolicy"]
    now = time.time() if now is None else now
    hours = policy["protectRecentHours"] if protect_hours is None else protect_hours
    protect = hours * 3600
    areas = []
    for relative in policy["regenerable"]:
        areas.append(scan_area(root, relative, "regenerable", now, protect, lambda p, m: True))

    generated = policy["generated"]
    generated_root = root / generated["root"]
    keep = {(generated_root / name) for name in generated["registered"]}
    areas.append(scan_area(root, generated["root"], "generated", now, protect,
                           lambda p, m: not any(Path(p) == k or Path(p).is_relative_to(k) for k in keep)))

    accumulating = re.compile(policy["accumulatingNamePattern"])
    for relative in policy["latestOnly"]:
        stale = set()
        base = root / relative
        if base.is_dir() and not is_link(base):
            for current, directories, _ in os.walk(base, followlinks=False):
                directories[:] = [d for d in directories if not is_link(Path(current) / d)]
                matched = [Path(current) / d for d in directories if accumulating.match(d)]
                if len(matched) > 1:
                    matched.sort(key=lambda p: p.stat().st_mtime, reverse=True)
                    stale.update(matched[1:])
                directories[:] = [d for d in directories if Path(current) / d not in stale]
        areas.append(scan_area(root, relative, "latest-only", now, protect,
                               lambda p, m, stale=stale: any(Path(p).is_relative_to(s) for s in stale)))

    prefixes = [Path(r).name for r in policy["evidence"]] + ["packages"]
    exact, partial = reference_paths(root, policy["referenceSources"], prefixes)
    decide = evidence_decider(root, policy, exact, partial)
    for relative in policy["evidence"]:
        areas.append(scan_area(root, relative, "evidence", now, protect, decide))
    # 实机采集的研究材料重采需要用户配合，整目录保留，只统计。
    for relative in policy.get("retained", []):
        areas.append(scan_area(root, relative, "retained", now, protect, lambda p, m: False))

    worktrees, _ = scan_worktrees(root, policy, now, protect)
    worktree_areas = [w["area"] for w in worktrees if "area" in w]

    root_real = os.path.realpath(root)
    if apply:
        for area in areas + worktree_areas:
            apply_area(root_real, area, now, protect)
        for worktree in worktrees:
            if worktree["state"] != "stale":
                continue
            # 已登记、未锁定且匹配 agentWorktreePattern 的旧工作树交给 Git 移除；有未提交修改时 Git 拒绝，保留原状。
            completed = subprocess.run(["git", "-C", str(root), "worktree", "remove", worktree["path"]],
                                       capture_output=True, text=True, encoding="utf-8", timeout=60)
            worktree["action"] = "removed" if completed.returncode == 0 else "kept: " + completed.stderr.strip()[:200]

    summaries = {area.relative: area.summary(apply) for area in areas}
    for worktree in worktrees:
        if "area" in worktree:
            worktree.update(worktree.pop("area").summary(apply))
    # 只列仍存在的被引用发行包路径；已清理的历史记录不再提示。
    missing_packages = sorted(r for r in exact if r.startswith("artifacts/packages/") and (root / r).exists())
    key = "deletedFiles" if apply else "deleteFiles"
    byte_key = "deletedBytes" if apply else "deleteBytes"
    every = list(summaries.values()) + [w for w in worktrees if "files" in w and "category" in w]
    totals = {"files": sum(s["files"] for s in every), key: sum(s[key] for s in every),
              byte_key: sum(s[byte_key] for s in every),
              "remainingFiles": sum(s["remainingFiles"] for s in every),
              "skippedRecent": sum(s["skippedRecent"] for s in every),
              "skippedLinks": sum(s["skippedLinks"] for s in every)}
    if apply:
        totals["failed"] = sum(s["failed"] for s in every)
    return {"mode": "apply" if apply else "dry-run", "protectRecentHours": hours, "areas": summaries,
            "worktrees": worktrees, "totals": totals,
            "referencedRegenerable": missing_packages}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--target", action="append", help="移交模式：登记范围内的仓库相对目录，可重复指定")
    parser.add_argument("--protect-hours", type=float, help="跳过该时长内有写入的文件，默认取规则值")
    parser.add_argument("--summary", action="store_true", help="只输出合计，省略各目录明细")
    args = parser.parse_args()
    try:
        if args.target:
            result = handoff(args.root.absolute(), load_rules(), args.target, args.apply)
        else:
            result = sweep(args.root.absolute(), load_rules(), args.apply, args.protect_hours)
            if args.summary:
                result = {"mode": result["mode"], "totals": result["totals"],
                          "worktrees": [{k: w[k] for k in ("path", "state", "action") if k in w}
                                        for w in result["worktrees"]]}
        print(json.dumps(result, ensure_ascii=False))
        return 0
    except (ValueError, OSError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
