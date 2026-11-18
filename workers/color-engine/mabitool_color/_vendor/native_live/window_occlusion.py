"""Find visible windows above the bound game that cover a physical screen rectangle.

Only windows that actually appear in a screen-DC capture count: hidden,
minimized, DWM-cloaked and WDA_EXCLUDEFROMCAPTURE windows are skipped, and a
window region (overlay widgets) limits the covered area to the region box.
Window titles are never read; only the owning process image name is kept so a
diagnostic can tell Moongate's own windows from others.
"""
import ctypes as C
from ctypes import wintypes as W
import ntpath

from mabitool_color._vendor.window_dpi import physical_pixel_context

GW_HWNDPREV = 3
DWMWA_CLOAKED = 14
WDA_EXCLUDEFROMCAPTURE = 0x11
SIMPLEREGION, COMPLEXREGION = 2, 3
GWL_EXSTYLE = -20
WS_EX_LAYERED, WS_EX_TRANSPARENT = 0x80000, 0x20


class _Api:
    def __init__(self):
        self.u = C.WinDLL('user32', use_last_error=True)
        self.g = C.WinDLL('gdi32')
        self.d = C.WinDLL('dwmapi')
        self.k = C.WinDLL('kernel32', use_last_error=True)
        for name, args, result in (
                ('GetWindow', [W.HWND, W.UINT], W.HWND),
                ('IsWindowVisible', [W.HWND], W.BOOL),
                ('IsIconic', [W.HWND], W.BOOL),
                ('GetWindowRect', [W.HWND, C.POINTER(W.RECT)], W.BOOL),
                ('GetWindowRgnBox', [W.HWND, C.POINTER(W.RECT)], C.c_int),
                ('GetWindowDisplayAffinity', [W.HWND, C.POINTER(W.DWORD)], W.BOOL),
                ('GetWindowThreadProcessId', [W.HWND, C.POINTER(W.DWORD)], W.DWORD),
                ('GetWindowLongW', [W.HWND, C.c_int], C.c_long)):
            fn = getattr(self.u, name); fn.argtypes = args; fn.restype = result
        self.d.DwmGetWindowAttribute.argtypes = [W.HWND, W.DWORD, C.c_void_p, W.DWORD]
        self.d.DwmGetWindowAttribute.restype = C.c_long
        self.k.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]; self.k.OpenProcess.restype = W.HANDLE
        self.k.QueryFullProcessImageNameW.argtypes = [W.HANDLE, W.DWORD, W.LPWSTR, C.POINTER(W.DWORD)]
        self.k.QueryFullProcessImageNameW.restype = W.BOOL
        self.k.CloseHandle.argtypes = [W.HANDLE]; self.k.CloseHandle.restype = W.BOOL

    def above(self, hwnd):
        current = self.u.GetWindow(hwnd, GW_HWNDPREV)
        while current:
            yield current
            current = self.u.GetWindow(current, GW_HWNDPREV)

    def captured_rect(self, hwnd):
        """Physical rectangle this window contributes to a screen capture, or None."""
        if not self.u.IsWindowVisible(hwnd) or self.u.IsIconic(hwnd):
            return None
        cloaked = W.DWORD()
        if self.d.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, C.byref(cloaked), C.sizeof(cloaked)) == 0 and cloaked.value:
            return None
        affinity = W.DWORD()
        if self.u.GetWindowDisplayAffinity(hwnd, C.byref(affinity)) and affinity.value == WDA_EXCLUDEFROMCAPTURE:
            return None
        rect = W.RECT()
        if not self.u.GetWindowRect(hwnd, C.byref(rect)):
            return None
        box = W.RECT()
        kind = self.u.GetWindowRgnBox(hwnd, C.byref(box))
        if kind in (SIMPLEREGION, COMPLEXREGION):
            return [rect.left + box.left, rect.top + box.top, rect.left + box.right, rect.top + box.bottom]
        # 整屏的分层点击穿透窗口（帧数显示一类的透明覆盖层）大多不画内容，不按整窗计入遮挡。
        click_through = WS_EX_LAYERED | WS_EX_TRANSPARENT
        if (self.u.GetWindowLongW(hwnd, GWL_EXSTYLE) & click_through) == click_through:
            return None
        return [rect.left, rect.top, rect.right, rect.bottom]

    def process_name(self, hwnd):
        pid = W.DWORD()
        self.u.GetWindowThreadProcessId(hwnd, C.byref(pid))
        handle = self.k.OpenProcess(0x1000, False, pid.value)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return None
        try:
            size = W.DWORD(32768); buffer = C.create_unicode_buffer(size.value)
            if not self.k.QueryFullProcessImageNameW(handle, 0, buffer, C.byref(size)):
                return None
            return ntpath.basename(buffer.value)
        finally:
            self.k.CloseHandle(handle)


def _intersection(a, b):
    left, top = max(a[0], b[0]), max(a[1], b[1])
    right, bottom = min(a[2], b[2]), min(a[3], b[3])
    return [left, top, right, bottom] if left < right and top < bottom else None


def find_occluders(hwnd, rect, *, api=None, limit=512):
    """Windows above ``hwnd`` whose captured area overlaps the physical ``rect``.

    Returns a list of ``dict(rect, overlap, process)``; empty when nothing covers it.
    """
    if not isinstance(hwnd, int) or hwnd <= 0:
        raise ValueError('Bound game window handle required for occlusion check')
    left, top, right, bottom = (int(round(v)) for v in rect)
    if right <= left or bottom <= top:
        raise ValueError('Positive occlusion rectangle required')
    api = api or _Api()
    found = []
    with physical_pixel_context():
        for count, other in enumerate(api.above(hwnd)):
            if count >= limit:
                break
            area = api.captured_rect(other)
            overlap = None if area is None else _intersection(area, [left, top, right, bottom])
            if overlap is not None:
                found.append(dict(rect=area, overlap=overlap, process=api.process_name(other)))
    return found


def palette_capture_rect(board, client_origin):
    """Board plus the card row above it, in physical screen pixels (same ROI as the card detector)."""
    l, t, r, b = board
    side = r - l
    ox, oy = client_origin
    return [ox + l - side * .06, oy + t - side * .30, ox + r + side * .06, oy + b + side * .03]
