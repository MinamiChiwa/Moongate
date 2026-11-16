"""原截图引擎的 Game 端口；截图和输入交由宿主统一代理。"""
import time
from types import SimpleNamespace

import numpy as np

from . import protocol
from ._vendor.input_gestures import drag_gesture, grouped_rotation_gesture, path_gesture, wheel_gesture
from ._vendor.platform_win import Interrupted


class ProxyGame:
    central_stop = True
    input_source = 'host_game_input_proxy'

    def __init__(self, stop, invoke, asset_root, run_id):
        self.stop, self.invoke, self.asset_root, self.run_id = stop, invoke, asset_root, run_id
        snapshot = self._call('game.snapshot', {})
        self.target = SimpleNamespace(title=snapshot['title'], pid=snapshot['pid'])
        self.hwnd = int(snapshot['hwnd'])
        self.initial = tuple(snapshot['geometry'])
        self._budget = None
        self._last_check = 0.
        self.last_gesture = self.last_input_trace = None

    def bind_budget(self, budget): self._budget = budget

    def _call(self, method, arguments):
        try: return self.invoke(method, arguments)
        except protocol.ProtocolError as exc:
            reason = {'input.bindingChanged': 'binding_changed', 'input.focusChanged': 'focus_lost',
                      'input.deadlineExceeded': 'deadline_exceeded', 'input.runStopped': 'user_stop'}.get(str(exc), 'proxy_failed')
            raise Interrupted(str(exc), reason=reason, recoverable=False) from exc

    def check(self):
        if self.stop.is_set(): raise Interrupted('已停止。', reason='user_stop')
        # 每次真实截图/输入仍由宿主校验；纯算法的高频检查合并至 10 Hz。
        if time.monotonic() - self._last_check >= .1:
            self._call('game.check', {})
            self._last_check = time.monotonic()

    def geometry(self):
        self.check()
        return self.initial

    def focus(self): self.check()

    def capture(self):
        self.check()
        frame = self._call('game.capture', {})
        data = protocol.read_asset(self.asset_root, frame['asset'], self.run_id, maximum=64 * 1024 * 1024)
        width, height = frame['width'], frame['height']
        if (type(width) is not int or type(height) is not int or
                (width, height) != self.initial[2:] or len(data) != width * height * 3):
            raise protocol.ProtocolError('capture.frameInvalid')
        self.captured_at = time.monotonic()
        return np.frombuffer(data, dtype=np.uint8).reshape(height, width, 3).copy()

    def capture_waiting(self): return self.capture()

    def release(self):
        if not self.stop.is_set():
            try: self._call('game.release', {})
            except Interrupted: pass

    def move_to(self, point):
        self.check()
        self._call('game.move', dict(x=round(point[0]), y=round(point[1])))

    def send(self, flags, dx=0, dy=0, data=0):
        self.check()
        if flags != 0x800 or dx or dy or data not in (-120, 120):
            raise protocol.ProtocolError('input.actionUnsupported')
        self._call('game.wheel', dict(delta=data))

    def perform_gesture(self, gesture):
        self.check()
        self.last_gesture = gesture
        self.last_input_trace = None
        if not gesture.has_effect: return False
        timing = gesture.timing
        descriptor = dict(kind=gesture.kind, points=[dict(x=x, y=y) for x, y in gesture.points],
            right=gesture.right, absolute=gesture.absolute, wheelSteps=gesture.wheel_steps,
            timing=dict(beforeDown=timing.before_down, afterDown=timing.after_down,
                        pointInterval=timing.point_interval, beforeUp=timing.before_up,
                        afterWheel=timing.after_wheel))
        remaining = self._budget.remaining() if self._budget is not None else 300.
        return self._call('game.gesture', dict(gesture=descriptor, remainingSeconds=remaining))['performed']

    def path(self, points, right=False, absolute=False):
        return self.perform_gesture(path_gesture(points, right, absolute))

    def drag(self, board, dx, dy): return self.perform_gesture(drag_gesture(board, dx, dy))
    def rotate(self, board, angle=30, anchor=None):
        return self.perform_gesture(grouped_rotation_gesture(board, angle, anchor))
    def wheel(self, board, steps, anchor=None):
        return self.perform_gesture(wheel_gesture(board, steps, anchor))
