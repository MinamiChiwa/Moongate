"""仓库维护脚本共用路径检查，拒绝联接与符号链接。"""
from pathlib import Path
import json
import os
import stat

ROOT = Path(__file__).resolve().parents[2]
RULES_PATH = ROOT / "scripts/maintenance/repository-rules.json"


def load_rules():
    return json.loads(RULES_PATH.read_text(encoding="utf-8"))


def is_link(path):
    metadata = path.lstat()
    return stat.S_ISLNK(metadata.st_mode) or bool(
        getattr(metadata, "st_file_attributes", 0) & 0x400)


def check_path(root, relative):
    root = Path(root).absolute()
    # 检查每一级祖先，禁止通过仓库上方的联接进入其他位置。
    for ancestor in [*reversed(root.parents), root]:
        if is_link(ancestor):
            raise ValueError(f"路径包含联接或符号链接：{ancestor}")
    parts = Path(relative).parts
    if not parts or Path(relative).is_absolute() or any(p in ("..", ".") for p in parts):
        raise ValueError(f"路径越界：{relative}")
    target = root.joinpath(*parts)
    if not target.is_relative_to(root) or target == root:
        raise ValueError(f"路径越界：{relative}")
    current = root
    for part in parts:
        current /= part
        if current.exists() or current.is_symlink():
            if is_link(current):
                raise ValueError(f"路径包含联接或符号链接：{current}")
    return target
