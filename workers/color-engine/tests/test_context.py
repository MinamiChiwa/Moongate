"""在无 GUI、无游戏输入条件下运行两条原算法入口。"""
from contextlib import ExitStack
import json
from pathlib import Path
import sys
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np

from mabitool_color import ColorEngineContext
from mabitool_color._vendor.profile_store import default_profile
from mabitool_color._vendor import screenshot_live, screenshot_control
from mabitool_color._vendor.native_palette_scoring import score_native_pose
from mabitool_color._vendor.screenshot_search import actual_result
from mabitool_color._vendor.platform_win import Interrupted


def session(count):
    return dict(pixels=tuple(np.full((254, 254, 3), 32, dtype=np.uint8) for _ in range(count)),
                picker_uv=[[(i + .5) / count, .5] for i in range(count)],
                color_preserve_ratio=0., region_count=count,
                initial_pose=dict(position=[0., 0.], scale=1., rotation_degrees=0.),
                capture_id='synthetic-offline')


class ReplayGame:
    def __init__(self, stop, target=None):
        self.stop = stop
        self.target = SimpleNamespace(title='offline', pid=1)
        self.hwnd = 1
        self.released = False
        self.frame = np.full((120, 200, 3), 255, dtype=np.uint8)

    def check(self):
        if self.stop.is_set():
            raise Interrupted('stopped', reason='user_stop')

    def capture_waiting(self):
        self.check()
        return self.frame.copy()

    def capture(self):
        return self.capture_waiting()

    def geometry(self):
        return [0, 0, 200, 120]

    def release(self):
        self.released = True


class NativeReplay:
    def __init__(self, count=3, *, cancel=False, unavailable=False):
        self.count = count
        self.cancel = cancel
        self.unavailable = unavailable
        self.closed = False
        self.releases = 0
        self.input_attempts = 0
        self.adapter = None

    def check(self): pass
    def resolve_target(self, target, **kw):
        kw['check']()
        return SimpleNamespace(title='offline', pid=1, hwnd=1)
    def prepare(self, *args): return self, dict(source='synthetic-offline')
    def require_input_capability(self, backend): pass
    def collect(self, backend, **kw):
        kw['event'](dict(event='armed'))
        kw['event'](dict(event='palette_captured'))
        if self.unavailable:
            return dict(stop_reason='no_active_session')
        return dict(stop_reason='passive_baseline_collected',
                    capture=dict(capture_folder='synthetic-offline'),
                    baseline=dict(motion=dict(settings=dict(minimum_scale=.1, maximum_scale=10.,
                        move_threshold=0., move_tolerance=0., scroll_zoom_ratio=.01))),
                    session_deadline_monotonic=time.monotonic() - 1.)
    def load_session(self, folder): return session(self.count)
    def create_io(self, backend, baseline, folder, *, stop):
        def check(deadline):
            if self.cancel:
                stop.request('user_stop')
                raise InterruptedError('stopped')
        self.check = check
        return self
    def release(self): self.releases += 1
    def close(self): self.closed = True


class ContextTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.events = []

    def context(self, profile=None, binding=None):
        return ColorEngineContext(profile or default_profile(), binding or {},
                                  self.temporary.name, self.events.append)

    def test_snapshot_preserves_priority_alternatives_disabled_and_binding(self):
        profile = default_profile()
        profile['priority_order'] = [2, 0, 1]
        profile['regions'][0].update(target='#a1b2c3', alt='ffffff， eaeaea')
        profile['regions'][1].update(enabled=False, target='invalid')
        profile['regions'][2].update(mode='相似颜色', tolerance=12)
        binding = dict(revision=3, target=dict(pid=4))
        c = self.context(profile, binding)
        profile['regions'][0]['target'] = '#000000'
        binding['target']['pid'] = 8
        self.assertEqual(c.rules[0], dict(enabled=True, colors=['#A1B2C3', '#FFFFFF', '#EAEAEA'],
            exact=True, tolerance=8., priority=2))
        self.assertEqual(c.rules[1], dict(enabled=False, colors=[], exact=True, tolerance=0, priority=3))
        self.assertFalse(c.rules[2]['exact'])
        self.assertEqual(c.rules[2]['tolerance'], 12)
        self.assertEqual(c.binding['target']['pid'], 4)
        exposed = c.rules
        exposed[0]['colors'].clear()
        self.assertEqual(len(c.rules[0]['colors']), 3)

    def test_missing_ports_rejected_before_start(self):
        for strategy in ('screenshot', 'native'):
            with self.subTest(strategy=strategy):
                c = self.context(dict(default_profile(), search_strategy=strategy))
                with self.assertRaises(ValueError): c.run()
                self.assertEqual(c.state, 'created')

    def test_pre_cancel_does_not_construct_game_and_context_cannot_restart(self):
        c = self.context()
        c.request_stop()
        def forbidden(*args, **kw): self.fail('已取消的任务创建了游戏代理')
        result = c.run(screenshot_game_factory=forbidden)
        self.assertEqual(result['stop_reason'], 'interrupted')
        self.assertEqual(c.state, 'stopped')
        with self.assertRaises(RuntimeError): c.run(screenshot_game_factory=forbidden)
        self.assertFalse(c.request_retry())

    def test_screenshot_real_controller_confirms_match_and_releases(self):
        for count in (2, 3):
            with self.subTest(count=count), ExitStack() as stack:
                self.events.clear()
                scene = SimpleNamespace(cards=[(10 + i * 50, 10, 40, 30) for i in range(count)],
                                        board=[0, 50, 200, 120], markers=np.zeros((count, 2)))
                # 仅替换画面识别/OCR 与按键读取；控制器、预算和结果判定使用原实现。
                for module in (screenshot_live, screenshot_control):
                    stack.enter_context(patch.object(module, 'recognize', return_value=scene))
                    stack.enter_context(patch.object(module, 'read_timer', return_value=100))
                    stack.enter_context(patch.object(module, 'timer_bar_signal', return_value=None))
                stack.enter_context(patch.object(screenshot_control, 'read_visible',
                    return_value=(['#202020'] * count, [])))
                stack.enter_context(patch.object(screenshot_control.u, 'GetAsyncKeyState', return_value=0))
                games = []
                def factory(*args, **kw):
                    games.append(ReplayGame(*args, **kw))
                    return games[-1]
                c = self.context()
                result = c.run(screenshot_game_factory=factory, wait_for_retry=False)
                self.assertTrue(result['accepted'])
                self.assertTrue(result['verified'])
                self.assertTrue(result['no_apply'])
                self.assertTrue(result['no_game_memory'])
                self.assertEqual(result['region_count'], count)
                self.assertGreaterEqual(result['actual_reads'], 2)
                self.assertEqual(result['actions'], 0)
                self.assertTrue(games[0].released)
                self.assertEqual(len(c.rules), 3)
                self.assertTrue(any(e['kind'] == 'screenshot_result' for e in self.events))
                self.assertTrue(any(e['data'].get('stage') == 'verify' for e in self.events))
                self.assertEqual([e['sequence'] for e in self.events], list(range(1, len(self.events) + 1)))
                json.dumps(self.events)

    def test_native_original_service_and_controller_handle_deadline_and_cancel(self):
        for count in (2, 3):
            for cancel in (False, True):
                with self.subTest(count=count, cancel=cancel):
                    self.events.clear()
                    runtime = NativeReplay(count, cancel=cancel)
                    c = self.context(dict(default_profile(), search_strategy='native'))
                    result = c.run(native_runtime=runtime)
                    self.assertEqual(result['stop_reason'], 'interrupted' if cancel else 'insufficient_time')
                    self.assertFalse(result['accepted'])
                    self.assertFalse(result['verified'])
                    self.assertEqual(result['actual_input_attempts'], 0)
                    self.assertTrue(runtime.closed)
                    self.assertGreaterEqual(runtime.releases, 1)
                    self.assertEqual(len(result['rules']), count)
                    self.assertEqual(len(c.rules), 3)
                    self.assertTrue(any(e['data'].get('stage') == 'waiting' for e in self.events))
                    self.assertTrue(any(e['kind'] == 'native_result' for e in self.events))
                    self.assertEqual(self.events[-1]['kind'], 'finished')

    def test_native_no_session_releases_backend(self):
        runtime = NativeReplay(unavailable=True)
        c = self.context(dict(default_profile(), search_strategy='native'))
        self.assertEqual(c.run(native_runtime=runtime)['stop_reason'], 'no_active_session')
        self.assertTrue(runtime.closed)

    def test_original_algorithms_keep_exact_alternative_and_similar_semantics(self):
        for count in (2, 3):
            for mode in ('精准 HEX', '相似颜色'):
                for target, alt in (('#202020', ''), ('#FFFFFF', '#202020'), ('#FFFFFF', '')):
                    with self.subTest(count=count, mode=mode, target=target, alt=alt):
                        profile = default_profile()
                        for row in profile['regions']: row.update(mode=mode, target=target, alt=alt)
                        rules = self.context(profile).rules[:count]
                        captured = session(count)
                        prediction = score_native_pose(captured, captured['initial_pose'], rules)
                        measured = actual_result(['#202020'] * count, rules)
                        self.assertEqual(prediction['predicted_accepted'], measured['accepted'])
                        self.assertEqual(prediction['colors'], ['#202020'] * count)
                        self.assertEqual(measured['accepted'], target == '#202020' or bool(alt))

    def test_runtime_failure_marks_failed_and_clears_retry(self):
        c = self.context()
        def fail(*args, **kw):
            self.assertTrue(c.request_retry())
            raise RuntimeError('offline transport failed')
        with self.assertRaisesRegex(RuntimeError, 'transport'):
            c.run(screenshot_game_factory=fail)
        self.assertEqual(c.state, 'failed')
        self.assertTrue(c.stop.is_set())
        self.assertFalse(c.consume_retry_request())

    def test_retry_is_consumed_once_and_stop_preserves_first_source(self):
        c = self.context()
        def factory(*args, **kw):
            self.assertTrue(c.request_retry())
            self.assertTrue(c.consume_retry_request())
            self.assertFalse(c.consume_retry_request())
            c.request_retry()
            c.request_stop('first')
            c.request_stop('second')
            self.assertFalse(c.consume_retry_request())
            raise Interrupted('stopped', reason='user_stop')
        result = c.run(screenshot_game_factory=factory)
        self.assertEqual(result['stop_reason'], 'interrupted')
        self.assertEqual(c.stop.diagnostic()['source'], 'first')

    def test_no_gui_modules_imported(self):
        self.assertFalse(any(name.startswith(('PySide6', 'PyQt', 'tkinter')) for name in sys.modules))


if __name__ == '__main__':
    unittest.main()
