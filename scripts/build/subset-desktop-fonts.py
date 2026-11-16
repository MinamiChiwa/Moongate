"""生成桌面前端随包分发的界面字体。

来源：Google Fonts 发行的 Noto Sans SC / Noto Sans TC 可变字体（SIL OFL 1.1）。
Avalonia 不按字重驱动可变字体，所以这里固定出常规、粗、特粗三个字重，
并裁剪到各自地区的常用字，单个文件保持在仓库 5 MB 上限以内。
裁掉的生僻字在运行时由另一份随包字体或系统字体补位。

用法：python scripts/build/subset-desktop-fonts.py <存放可变字体的目录>
环境：Python 3.12；先安装 scripts/build/font-generation-requirements.txt。
"""

import hashlib
import sys
from pathlib import Path

import fontTools
from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

SOURCES = {
    "NotoSansSC": ("NotoSansSC-VF.ttf", "a3041811a78c361b1de50f953c805e0244951c21c5bd412f7232ef0d899af0da"),
    "NotoSansTC": ("NotoSansTC-VF.ttf", "864727d210d54f2537bbe23b3a839436c3992af72de9322af5270897246bd44f"),
}
WEIGHTS = {"Regular": 400, "Bold": 700, "Black": 900}
OUTPUT = Path(__file__).resolve().parents[2] / "frontends/desktop/src/MabiTool.Desktop/Assets/Fonts"
FONTTOOLS_VERSION = Path(__file__).with_name("font-generation-requirements.txt").read_text(
    encoding="utf-8").strip().removeprefix("fonttools==")


def decode_range(codec, lead_range, trail_ranges):
    chars = set()
    for lead in lead_range:
        for trail_range in trail_ranges:
            for trail in trail_range:
                try:
                    chars.add(ord(bytes([lead, trail]).decode(codec)))
                except (UnicodeDecodeError, TypeError):
                    pass
    return chars


def common_symbols():
    chars = set(range(0x20, 0x7F)) | set(range(0xA0, 0x100))
    chars |= set(range(0x2000, 0x2070)) | set(range(0x2190, 0x2200)) | set(range(0x2460, 0x2500))
    chars |= set(range(0x25A0, 0x2600)) | set(range(0x3000, 0x3040)) | set(range(0xFF00, 0xFFF0))
    chars |= {0x00D7, 0x2026, 0x2030, 0x2103, 0x2116, 0x2122, 0x2212, 0x2260, 0x2264, 0x2265, 0x221E}
    return chars


def charset(name):
    # 简体：GB2312 全部汉字与符号。繁体：Big5 常用字与次常用字。
    if name == "NotoSansSC":
        return decode_range("gb2312", range(0xA1, 0xF8), [range(0xA1, 0xFF)]) | common_symbols()
    trails = [range(0x40, 0x7F), range(0xA1, 0xFF)]
    return decode_range("big5", range(0xA1, 0xFA), trails) | common_symbols()


def save_font(font, target):
    font.recalcTimestamp = False
    font.save(target)


def build(source_dir):
    if sys.version_info[:2] != (3, 12) or fontTools.__version__ != FONTTOOLS_VERSION:
        raise SystemExit("字体生成须使用 Python 3.12 和 fonttools==" + FONTTOOLS_VERSION)
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for name, (file_name, sha256) in SOURCES.items():
        source = Path(source_dir) / file_name
        actual = hashlib.sha256(source.read_bytes()).hexdigest()
        if actual != sha256:
            raise SystemExit(f"{file_name} 的 SHA-256 与登记值不同：{actual}")
        unicodes = charset(name)
        for style, weight in WEIGHTS.items():
            font = instancer.instantiateVariableFont(TTFont(source, recalcTimestamp=False), {"wght": weight}, updateFontNames=True)
            options = subset.Options()
            options.layout_features = ["*"]
            options.name_IDs = ["*"]
            options.name_languages = ["*"]
            options.notdef_outline = True
            subsetter = subset.Subsetter(options)
            subsetter.populate(unicodes=unicodes)
            subsetter.subset(font)
            target = OUTPUT / f"{name}-{style}.ttf"
            save_font(font, target)
            print(f"{target.name}: {target.stat().st_size} 字节，{len(font.getBestCmap())} 个字符")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    build(sys.argv[1])
