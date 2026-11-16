#!/usr/bin/env python3
"""检查 artifacts/ 与 .claude/worktrees 的留存预算。

只读：统计文件数、体积和留存时间，超出预算时逐目录列出并以退出码 1 结束。
可回收目录只计保护期外的文件；保护期内的文件由运行中的任务持有，单独报告。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

# 各分类的文件数上限；调整时同步更新仓库文档中的生成物预算说明。
MAX_FILE_COUNTS = {
    "packages": 500, "test-results": 800, "reports": 300, "research": 300, "probes": 100,
    "generated": 200, "build": 8_000, "tmp": 200, "test-data": 200, "pending-cleanup": 200,
}
MAX_WORKTREE_FILES = 20_000   # .claude/worktrees 中 agent-* 与未登记残留合计；其他已登记工作树不计
MAX_TOTAL_FILES = 12_000      # artifacts/ 合计（含 build/）
MAX_BYTES = 3 * 1024 ** 3     # artifacts/ 不含 build/ 的体积
MAX_BUILD_BYTES = 8 * 1024 ** 3
MAX_PACKAGE_VERSIONS = 3      # packages/ 下保留的发行包目录数
TMP_MAX_AGE_DAYS = 3
PENDING_MAX_AGE_DAYS = 7
CLEANABLE = {"packages", "test-results", "reports", "probes", "generated",
             "tmp", "test-data", "pending-cleanup"}
REGISTERED = set(MAX_FILE_COUNTS)
RULES = Path(__file__).resolve().with_name("repository-rules.json")


def is_link(entry: os.DirEntry) -> bool:
    if entry.is_symlink():
        return True
    try:
        return bool(getattr(entry.stat(follow_symlinks=False), "st_file_attributes", 0) & 0x400)
    except OSError:
        return True


def touched_at(metadata: os.stat_result) -> float:
    created = getattr(metadata, "st_birthtime", None)
    if created is None and os.name == "nt":
        created = metadata.st_ctime
    return max(metadata.st_mtime, created or 0)


def tree_stats(path: Path, recent_after: float = float("inf")) -> tuple[int, int, int]:
    """返回 (文件数, 字节, 保护期内文件数)，不跟随联接与符号链接。"""
    files = size = recent = 0
    stack = [path]
    while stack:
        current = stack.pop()
        try:
            with os.scandir(current) as entries:
                for entry in entries:
                    if is_link(entry):
                        continue
                    if entry.is_dir(follow_symlinks=False):
                        stack.append(Path(entry.path))
                    else:
                        metadata = entry.stat(follow_symlinks=False)
                        files += 1
                        size += metadata.st_size
                        if touched_at(metadata) >= recent_after:
                            recent += 1
        except OSError:
            continue
    return files, size, recent


def newest_mtime(path: Path) -> float:
    newest = path.stat().st_mtime
    for root, _dirs, names in os.walk(path):
        for name in names:
            try:
                newest = max(newest, os.stat(os.path.join(root, name)).st_mtime)
            except OSError:
                continue
    return newest


def protect_hours() -> float:
    try:
        return float(json.loads(RULES.read_text(encoding="utf-8"))["artifactPolicy"]["protectRecentHours"])
    except (OSError, KeyError, ValueError):
        return 2.0


def foreign_worktrees(root: Path) -> set[str]:
    """已登记但不匹配 agentWorktreePattern 的工作树目录名；Git 不可用时视为没有，全部计入上限。"""
    try:
        pattern = re.compile(json.loads(RULES.read_text(encoding="utf-8"))["artifactPolicy"]["agentWorktreePattern"])
        output = subprocess.run(["git", "-C", str(root), "worktree", "list", "--porcelain"], capture_output=True,
                                text=True, encoding="utf-8", timeout=20, check=True).stdout
    except (OSError, KeyError, ValueError, subprocess.SubprocessError):
        return set()
    base = os.path.normcase(os.path.abspath(root / ".claude" / "worktrees"))
    names = set()
    for line in output.splitlines():
        if line.startswith("worktree "):
            path = os.path.normcase(os.path.abspath(line[9:]))
            if os.path.dirname(path) == base and not pattern.match(os.path.basename(path)):
                names.add(os.path.basename(path))
    return names


def check(root: Path, now: float | None = None, hours: float | None = None) -> dict:
    now = time.time() if now is None else now
    hours = protect_hours() if hours is None else hours
    recent_after = now - hours * 3600
    artifacts = root / "artifacts"
    violations: list[str] = []
    areas: dict[str, dict[str, int]] = {}
    total_files = size = 0

    if artifacts.is_dir():
        for child in sorted(artifacts.iterdir()):
            if not child.is_dir():
                violations.append(f"artifacts 根部的散落文件：{child.name}")
                continue
            count, total, recent = tree_stats(child, recent_after)
            counted = count - recent if child.name in CLEANABLE else count
            areas[child.name] = {"files": count, "bytes": total, "recentFiles": recent, "countedFiles": counted}
            total_files += counted
            limit = MAX_FILE_COUNTS.get(child.name)
            if limit is None:
                violations.append(f"未登记的 artifacts 子目录：{child.name}")
            elif counted > limit:
                violations.append(f"artifacts/{child.name} 文件数 {counted} 超过 {limit}")
            if child.name == "build":
                if total > MAX_BUILD_BYTES:
                    violations.append(f"artifacts/build 体积 {total} 超过 {MAX_BUILD_BYTES}")
                continue
            size += total

        if total_files > MAX_TOTAL_FILES:
            violations.append(f"artifacts 合计文件数 {total_files} 超过 {MAX_TOTAL_FILES}")
        if size > MAX_BYTES:
            violations.append(f"artifacts 体积 {size} 超过 {MAX_BYTES}（不含 build）")

        packages = artifacts / "packages"
        if packages.is_dir():
            versions = [p.name for p in packages.iterdir() if p.is_dir()]
            if len(versions) > MAX_PACKAGE_VERSIONS:
                violations.append(f"artifacts/packages 保留了 {len(versions)} 个发行包目录，上限 {MAX_PACKAGE_VERSIONS}")

        for name, limit in (("tmp", TMP_MAX_AGE_DAYS), ("test-data", TMP_MAX_AGE_DAYS),
                            ("pending-cleanup", PENDING_MAX_AGE_DAYS)):
            area = artifacts / name
            if not area.is_dir():
                continue
            for entry in area.iterdir():
                if not entry.is_dir():
                    continue
                age_days = (now - newest_mtime(entry)) / 86400
                if age_days > limit:
                    violations.append(f"artifacts/{name}/{entry.name} 已 {age_days:.0f} 天无写入，上限 {limit} 天")

    worktrees = root / ".claude" / "worktrees"
    worktree_files = foreign_files = 0
    if worktrees.is_dir():
        foreign = foreign_worktrees(root)
        for child in sorted(worktrees.iterdir()):
            if child.is_dir() and not child.is_symlink():
                count, total, _ = tree_stats(child)
                is_foreign = child.name in {n for n in foreign if os.path.normcase(n) == os.path.normcase(child.name)}
                areas[".claude/worktrees/" + child.name] = {"files": count, "bytes": total, "foreign": is_foreign}
                if is_foreign:
                    foreign_files += count
                else:
                    worktree_files += count
        if worktree_files > MAX_WORKTREE_FILES:
            violations.append(f".claude/worktrees 文件数 {worktree_files} 超过 {MAX_WORKTREE_FILES}")

    return {"artifactFiles": total_files, "bytes": size, "worktreeFiles": worktree_files,
            "foreignWorktreeFiles": foreign_files,
            "protectRecentHours": hours, "areas": areas, "violations": violations}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--violations-only", action="store_true", help="只输出超限项，供提交钩子使用")
    args = parser.parse_args()
    result = check(args.root)
    if args.violations_only:
        for line in result["violations"]:
            print(line)
    else:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    return 1 if result["violations"] else 0


if __name__ == "__main__":
    sys.exit(main())
