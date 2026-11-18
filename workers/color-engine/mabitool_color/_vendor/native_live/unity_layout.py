"""Locate the UnityPlayer Screen global and mouse getter from static file features.

Used only when UnityPlayer's hash is not registered. Object field offsets
(Screen extent, window handle, viewport, RectTransform/Transform caches) are
taken from the registered layout, so the file must report the same Unity
engine version. Located addresses are checked again at run time: the Screen
object's window handle must equal the bound game window and GameAssembly's
cached mouse getter must equal the located entry.
"""
import re
import struct

import pefile

from .build_adapters import BuildUnsupported, UNITY_LAYOUT, UnityLayout

ENGINE_VERSION = (2021, 3, 51)
MOUSE_ICALL = b'UnityEngine.Input::get_mousePosition_Injected\x00'
# ScreenManager construction: allocate the 0x170-byte object, construct it and
# store the pointer into the global that the Screen getters read.
_SCREEN_INIT = re.compile(
    rb'\xB9\x70\x01\x00\x00'          # mov ecx, 0x170
    rb'\xE8....'                      # call allocator
    rb'\x48\x85\xC0'                  # test rax, rax
    rb'\x74.'                         # je
    rb'\x48\x8B\xC8'                  # mov rcx, rax
    rb'\xE8....'                      # call constructor
    rb'\x48\x89\x05(....)',           # mov [rip+global], rax
    re.DOTALL)
SCREEN_OBJECT_SIZE = 0x170


class UnityEngineUnsupported(BuildUnsupported):
    """The UnityPlayer cannot be mapped onto a registered engine layout."""
    capability_reason = 'unity_engine'

    def __init__(self, hashes, detail):
        super().__init__(hashes, 'Unsupported UnityPlayer build; native layout requires review: ' + detail)
        self.detail = detail


class UnityLayoutMismatch(ValueError):
    """A located layout failed its run-time check against the live process."""
    capability_reason = 'unity_engine'


def _sections(pe):
    text = rdata = data = None
    for section in pe.sections:
        name = section.Name.rstrip(b'\x00')
        if name == b'.text': text = section
        elif name == b'.rdata': rdata = section
        elif name == b'.data': data = section
    if text is None or rdata is None or data is None:
        raise ValueError('UnityPlayer sections missing')
    return text, rdata, data


def _contains(section, rva, size=1):
    return section.VirtualAddress <= rva and rva + size <= section.VirtualAddress + max(section.Misc_VirtualSize, section.SizeOfRawData)


def engine_version(pe):
    pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_RESOURCE']])
    infos = getattr(pe, 'VS_FIXEDFILEINFO', None)
    if not infos:
        raise ValueError('UnityPlayer version resource missing')
    info = infos[0]
    return (info.FileVersionMS >> 16, info.FileVersionMS & 0xffff, info.FileVersionLS >> 16)


def _mouse_getter(pe, text, rdata, data, raw, check):
    base = pe.OPTIONAL_HEADER.ImageBase
    start = rdata.PointerToRawData
    found = [m.start() for m in re.finditer(re.escape(MOUSE_ICALL), raw[start:start + rdata.SizeOfRawData])]
    if len(found) != 1:
        raise ValueError('Mouse icall name not unique')
    name_rva = rdata.VirtualAddress + found[0]
    table = raw[data.PointerToRawData:data.PointerToRawData + data.SizeOfRawData]
    slots = [m.start() for m in re.finditer(re.escape(struct.pack('<Q', base + name_rva)), table) if m.start() % 8 == 0]
    if len(slots) != 1:
        raise ValueError('Mouse icall name table entry not unique')
    check()

    def is_name(offset):
        if offset < 0 or offset + 8 > len(table):
            return False
        rva = struct.unpack_from('<Q', table, offset)[0] - base
        if not _contains(rdata, rva, 2):
            return False
        text_offset = rdata.PointerToRawData + rva - rdata.VirtualAddress
        entry = raw[text_offset:text_offset + 160].split(b'\x00', 1)[0]
        return b'::' in entry and all(32 < c < 127 for c in entry)

    first = last = slots[0]
    while is_name(first - 8):
        first -= 8
    while is_name(last + 8):
        last += 8
    count = (last - first) // 8 + 1
    functions = table[last + 8:last + 8 + count * 8]
    if len(functions) != count * 8:
        raise ValueError('Mouse icall function table truncated')
    pointers = [value - base for value in struct.unpack('<%dQ' % count, functions)]
    if any(not _contains(text, rva) for rva in pointers):
        raise ValueError('Mouse icall function table not in code')
    return pointers[(slots[0] - first) // 8]


def _screen_global(pe, text, data, raw, check):
    code = raw[text.PointerToRawData:text.PointerToRawData + text.SizeOfRawData]
    targets = set()
    for match in _SCREEN_INIT.finditer(code):
        end = match.end()
        targets.add(text.VirtualAddress + end + struct.unpack('<i', match.group(1))[0])
    check()
    if len(targets) != 1:
        raise ValueError('Screen manager construction not unique')
    target = targets.pop()
    if not _contains(data, target, 8) or target % 8:
        raise ValueError('Screen manager global not in writable data')
    # Exactly one getter function: mov rax, [rip+global]; ret
    accessors = 0
    for match in re.finditer(rb'\x48\x8B\x05(....)\xC3', code, re.DOTALL):
        if text.VirtualAddress + match.start() + 7 + struct.unpack('<i', match.group(1))[0] == target:
            accessors += 1
    if accessors != 1:
        raise ValueError('Screen manager accessor not unique')
    return target


def locate_unity_layout(path, sha256, *, check=lambda: None):
    """Return a located UnityLayout for this file, or raise UnityEngineUnsupported."""
    hashes = dict(unityplayer=sha256)
    check()
    try:
        with open(path, 'rb') as stream:
            raw = stream.read()
        pe = pefile.PE(data=raw, fast_load=True)
        try:
            version = engine_version(pe)
            if version != ENGINE_VERSION:
                raise UnityEngineUnsupported(hashes, 'engine version %d.%d.%d differs' % version)
            text, rdata, data = _sections(pe)
            check()
            mouse = _mouse_getter(pe, text, rdata, data, raw, check)
            screen = _screen_global(pe, text, data, raw, check)
        finally:
            pe.close()
    except UnityEngineUnsupported:
        raise
    except (OSError, ValueError, IndexError, struct.error, pefile.PEFormatError) as exc:
        raise UnityEngineUnsupported(hashes, str(exc)) from exc
    if UNITY_LAYOUT.viewport_offset + 24 > SCREEN_OBJECT_SIZE:
        raise UnityEngineUnsupported(hashes, 'Screen field offsets exceed object size')
    return UnityLayout('located-2021.3.51-' + sha256[:12], sha256, mouse_getter_rva=mouse,
                       screen_global_rva=screen, viewport_offset=UNITY_LAYOUT.viewport_offset,
                       mouse_cache_rva=None, located=True)
