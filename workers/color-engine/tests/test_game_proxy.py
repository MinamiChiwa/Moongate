"""原手势回放、资产截图与原控制器的代理对照；不调用桌面设备。"""
from contextlib import ExitStack, contextmanager
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mabitool_color import ColorEngineContext
from mabitool_color import protocol
from mabitool_color.game_proxy import ProxyGame
from mabitool_color._vendor import platform_win, screenshot_live, screenshot_control
from mabitool_color._vendor.input_gestures import (drag_gesture, grouped_rotation_gesture,
    path_gesture, wheel_gesture)
from mabitool_color._vendor.profile_store import default_profile


def gestures():
    board = (10, 10, 190, 110)
    return [drag_gesture(board, 40, -15), drag_gesture(board, -80, 20),
            grouped_rotation_gesture(board, 3), grouped_rotation_gesture(board, -30),
            grouped_rotation_gesture(board, 0), wheel_gesture(board, 3), wheel_gesture(board, -2),
            wheel_gesture(board, 0),
            path_gesture([(1, 2), (1, 2), (4, 6), (4, 6), (5, 3)], True, False),
            path_gesture([(1, 2), (1, 2), (4, 6)], False, True),
            path_gesture([(1, 2), (1, 2)])]


class OriginalRecorder:
    capture_input_trace = False
    def __init__(self): self.trace = []
    def check(self): pass
    def move_to(self, point): self.trace.append(f'absolute:{100 + point[0]},{200 + point[1]}')
    def send(self, flags, dx=0, dy=0, data=0):
        if flags == 1: self.trace.append(f'relative:{dx},{dy}')
        elif flags == 0x800: self.trace.append(f'wheel:{data}')
        else: self.trace.append({2: 'down:MouseLeft', 4: 'up:MouseLeft',
                                 8: 'down:MouseRight', 16: 'up:MouseRight'}[flags])


@contextmanager
def screenshot_scene(count):
    """只替换识别输入；控制器、预算与颜色规则继续运行原实现。"""
    scene = SimpleNamespace(cards=[(10 + i * 50, 10, 40, 30) for i in range(count)],
        board=[0, 50, 200, 120], markers=np.zeros((count, 2)))
    with ExitStack() as stack:
        for module in (screenshot_live, screenshot_control):
            for name, value in [('recognize', scene), ('read_timer', 100), ('timer_bar_signal', None)]:
                stack.enter_context(patch.object(module, name, return_value=value))
        stack.enter_context(patch.object(screenshot_control, 'read_visible', return_value=(['#202020'] * count, [])))
        stack.enter_context(patch.object(screenshot_control.u, 'GetAsyncKeyState', side_effect=AssertionError('绕过中央停止')))
        yield


class OfflinePort:
    def __init__(self, root, *, count=2):
        self.root, self.run = Path(root), 'a' * 32
        (self.root / self.run).mkdir(exist_ok=True)
        self.calls = []
        self.image = bytes([255]) * (200 * 120 * 3)
        self.count = count

    def __call__(self, method, args):
        self.calls.append((method, args))
        if method == 'game.snapshot': return dict(title='offline', pid=1, hwnd='1', geometry=[100, 200, 200, 120])
        if method == 'game.gesture': return dict(performed=True)
        if method == 'game.capture':
            asset = 'b' * 32
            (self.root / self.run / (asset + '.blob')).write_bytes(self.image)
            return dict(width=200, height=120, asset=dict(assetId=asset, runId=self.run,
                sizeBytes=len(self.image), sha256=hashlib.sha256(self.image).hexdigest(),
                expiresAtUnixMs=int(time.time() * 1000 + 120_000)))
        return {}


def replay_rows():
    """供 C# 直接执行原 Python 描述并比较原输入轨迹。"""
    rows = []
    with tempfile.TemporaryDirectory() as root:
        port = OfflinePort(root)
        proxy = ProxyGame(threading.Event(), port, root, port.run)
        for gesture in gestures():
            recorder = OriginalRecorder()
            waits = []
            with patch.object(platform_win.time, 'sleep', side_effect=waits.append):
                performed = platform_win.Game._perform_gesture(recorder, gesture)
            port.calls.clear()
            actual = proxy.perform_gesture(gesture)
            calls = [args for name, args in port.calls if name == 'game.gesture']
            # 原算法认定无效果时，代理不能发送旋转的径向预备动作。
            if not performed:
                assert not actual and not calls and not recorder.trace
                continue
            assert actual and len(calls) == 1
            rows.append(dict(gesture=calls[0]['gesture'], trace=recorder.trace, waits=waits))
    return rows


class ProxyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.port = OfflinePort(self.temp.name)
        self.stop = threading.Event()
        self.game = ProxyGame(self.stop, self.port, self.temp.name, self.port.run)

    def test_original_descriptor_coordinates_and_every_wait_are_preserved(self):
        rows = replay_rows()
        self.assertEqual(len(rows), 8)
        for row in rows:
            gesture, waits = row['gesture'], row['waits']
            t = gesture['timing']
            expected = ([t['pointInterval']] * abs(gesture['wheelSteps']) + [t['afterWheel']]
                if gesture['kind'] == 'wheel' else
                [t['beforeDown'], t['afterDown']] + [t['pointInterval']] * (len(gesture['points']) - 1) + [t['beforeUp']])
            self.assertEqual(waits, expected)
        self.assertTrue(any(r['gesture']['kind'] == 'rotate' for r in rows))

    def test_budget_is_read_for_each_gesture_and_only_typed_inputs_are_sent(self):
        remaining = iter([10., 3.])
        self.game.bind_budget(SimpleNamespace(remaining=lambda: next(remaining)))
        for _ in range(2): self.game.drag((10, 10, 190, 110), 10, 0)
        self.assertEqual([a['remainingSeconds'] for m, a in self.port.calls if m == 'game.gesture'], [10., 3.])
        self.game.move_to((2.2, 3.7))
        self.game.send(0x800, data=-120)
        self.assertIn(('game.move', dict(x=2, y=4)), self.port.calls)
        self.assertIn(('game.wheel', dict(delta=-120)), self.port.calls)
        before = len(self.port.calls)
        with self.assertRaises(protocol.ProtocolError): self.game.send(2)
        self.assertEqual(len(self.port.calls), before)

    def test_rgb_asset_is_verified_and_returned_as_independent_array(self):
        image = self.game.capture()
        self.assertEqual(image.shape, (120, 200, 3))
        image[0, 0] = 0
        self.assertEqual(self.game.capture()[0, 0].tolist(), [255, 255, 255])
        self.port.image = b'bad'
        with self.assertRaisesRegex(protocol.ProtocolError, 'capture.frameInvalid'): self.game.capture()

    def test_cancel_prevents_io_and_host_errors_keep_typed_reason(self):
        for code, reason in [('input.bindingChanged', 'binding_changed'), ('input.focusChanged', 'focus_lost'),
                             ('input.deadlineExceeded', 'deadline_exceeded'), ('input.runStopped', 'user_stop')]:
            with self.subTest(code=code), patch.object(self.game, 'invoke', side_effect=protocol.ProtocolError(code)):
                with self.assertRaises(platform_win.Interrupted) as error: self.game.move_to((1, 2))
                self.assertEqual(error.exception.reason, reason)
                self.assertFalse(error.exception.recoverable)
        self.stop.set()
        before = len(self.port.calls)
        with self.assertRaises(platform_win.Interrupted): self.game.capture()
        with self.assertRaises(platform_win.Interrupted): self.game.drag((10, 10, 190, 110), 10, 0)
        self.assertEqual(len(self.port.calls), before)

    def test_initial_snapshot_uses_same_error_mapping_and_release_preserves_stop(self):
        def rejected(method, args): raise protocol.ProtocolError('input.bindingChanged')
        with self.assertRaises(platform_win.Interrupted) as error:
            ProxyGame(self.stop, rejected, self.temp.name, self.port.run)
        self.assertEqual(error.exception.reason, 'binding_changed')
        self.game.invoke = rejected
        self.game.release()

    def test_original_controller_confirms_same_colors_through_proxy(self):
        for count in (2, 3):
            with self.subTest(count=count), screenshot_scene(count):
                context = ColorEngineContext(default_profile(), {}, Path(self.temp.name) / 'results', lambda _: None)
                result = context.run(screenshot_game_factory=lambda stop, target=None: ProxyGame(
                    stop, self.port, self.temp.name, self.port.run), wait_for_retry=False)
                self.assertTrue(result['verified'])
                self.assertTrue(result['accepted'])
                self.assertEqual(result['actual_colors'], ['#202020'] * count)
                self.assertEqual(result['region_count'], count)
                self.assertGreaterEqual(result['actual_reads'], 2)
                self.assertTrue(result['no_game_memory'])
                self.assertIn(('game.release', {}), self.port.calls)


if __name__ == '__main__':
    if '--replay-json' in sys.argv: print(json.dumps(replay_rows(), ensure_ascii=False))
    else: unittest.main()
