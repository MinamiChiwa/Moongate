"""精准授权、进程身份、失败隔离及原闭环代理接入的离线验证。"""
from contextlib import ExitStack
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mabitool_color import ColorEngineContext, protocol
from mabitool_color.game_proxy import ProxyGame
from mabitool_color.native_runtime import ProxyNativeRuntime, NativeSourceUnavailable
from mabitool_color._vendor.profile_store import default_profile
from mabitool_color._vendor.native_live import service, native_provider_backend, project_closed_loop_io
from mabitool_color._vendor.native_live.project_probe_io import ProjectProbeIO
from mabitool_color._vendor.native_live.build_adapters import BuildUnsupported, TW_ADAPTER, KR_ADAPTER
from mabitool_color._vendor.input_gestures import PointerGesture
from test_game_proxy import OfflinePort, OriginalRecorder


class NativePort(OfflinePort):
    def __init__(self, root):
        super().__init__(root)
        self.grant = dict(pid=1, hwnd='1', server='tw', processCreationTime='123456789')
        self.authorized = True

    def __call__(self, method, args):
        if method == 'game.nativeAccess':
            self.calls.append((method, args))
            if not self.authorized:
                raise protocol.ProtocolError('permission.missing')
            return self.grant.copy()
        return super().__call__(method, args)


class NativeRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.port = NativePort(self.temp.name)
        self.stop = threading.Event()
        self.game = ProxyGame(self.stop, self.port, self.temp.name, self.port.run)
        self.runtime = ProxyNativeRuntime(self.game)

    def resolve(self):
        return self.runtime.resolve_target(None, check=self.runtime.check)

    def context(self):
        return ColorEngineContext(dict(default_profile(), search_strategy='native'), {},
                                  Path(self.temp.name) / 'results', lambda _: None)

    def test_missing_permission_rejects_before_reader_and_screenshot_can_still_run(self):
        self.port.authorized = False
        with patch.object(service, 'CurrentBuildBackend', side_effect=AssertionError('未经授权打开进程')):
            result = self.context().run(native_runtime=self.runtime)
        self.assertEqual(result['stop_reason'], 'unavailable')
        self.assertEqual(result['unavailable_reason'], 'permission.missing')
        self.assertFalse(result['accepted'])
        from test_game_proxy import screenshot_scene
        with screenshot_scene(2):
            result = ColorEngineContext(default_profile(), {}, Path(self.temp.name)/'screenshot', lambda _: None).run(
                screenshot_game_factory=lambda stop, target=None: ProxyGame(stop, self.port, self.temp.name, self.port.run),
                wait_for_retry=False)
        self.assertTrue(result['accepted'])
        self.assertTrue(result['no_game_memory'])

    def test_bound_identity_cannot_be_replaced(self):
        for changes in (dict(pid=2), dict(hwnd='2'), dict(server='unknown'), dict(processCreationTime='-1'),
                        dict(processCreationTime='0'), dict(processCreationTime='01'),
                        dict(processCreationTime='18446744073709551616')):
            original = self.port.grant.copy()
            self.port.grant.update(changes)
            with self.assertRaises(NativeSourceUnavailable): self.resolve()
            self.port.grant = original
        with self.assertRaises(NativeSourceUnavailable):
            self.runtime.resolve_target(dict(pid=2), check=self.runtime.check)
        with self.assertRaises(NativeSourceUnavailable):
            self.runtime.resolve_target(None, server='kr', check=self.runtime.check)

    def test_prepare_forwards_creation_identity_and_releases_rejected_backend(self):
        target = self.resolve()
        backend = SimpleNamespace(adapter=TW_ADAPTER, close=Mock())
        with patch.object(service, '_prepare', return_value=(backend, {'stage': 'complete'})) as prepare:
            actual, record = self.runtime.prepare(target.pid, self.temp.name, self.runtime.check, target.hwnd)
            self.assertIs(actual, backend)
            self.assertEqual(record['stage'], 'complete')
            self.assertEqual(prepare.call_args.kwargs, dict(expected_creation=123456789))
            backend.close.assert_not_called()
            backend.adapter = KR_ADAPTER
            with self.assertRaises(NativeSourceUnavailable) as error:
                self.runtime.prepare(target.pid, self.temp.name, self.runtime.check, target.hwnd)
            self.assertEqual(error.exception.capability_reason, 'gameBuild.incompatible')
            backend.close.assert_called_once()
            self.assertFalse(getattr(backend, 'validation_input_enabled', False))

    def test_build_failure_is_independent_and_keeps_domain_reason(self):
        for failure, reason in ((BuildUnsupported(dict(gameassembly='0'*64)), 'gameBuild.incompatible'),
                (native_provider_backend.ProcessBindingChanged('restarted'), 'input.bindingChanged')):
            with self.subTest(reason=reason), patch.object(service, '_prepare', side_effect=failure):
                result = self.context().run(native_runtime=self.runtime)
                self.assertEqual(result['stop_reason'], 'unavailable')
                self.assertEqual(result['unavailable_reason'], reason)
                self.assertEqual(result['actual_input_attempts'], 0)

    def test_reused_pid_is_rejected_before_hashing_or_palette_reads(self):
        reader = SimpleNamespace(h=1, pid=1, close=Mock())
        def process_times(handle, creation, *unused):
            creation._obj.dwLowDateTime = 22
            creation._obj.dwHighDateTime = 0
            return True
        with patch.object(native_provider_backend, 'GuardedReader', return_value=reader), \
                patch.object(native_provider_backend.K, 'GetProcessTimes', side_effect=process_times), \
                patch.object(native_provider_backend, '_hash_build_file', side_effect=AssertionError('已读取错误进程构建')):
            with self.assertRaises(native_provider_backend.ProcessBindingChanged):
                native_provider_backend.CurrentBuildBackend(1, expected_creation=11)
        reader.close.assert_called_once()

    def test_prepare_closes_reader_on_window_layout_failure(self):
        self.resolve()
        backend = SimpleNamespace(adapter=TW_ADAPTER, close=Mock(),
            observe_window_context=Mock(return_value=dict(extent_one_to_one=False)))
        with ExitStack() as stack:
            for name in ('configure_ocr', 'prepare_timer_assets', 'prepare_hex_assets'):
                stack.enter_context(patch.object(service, name, return_value={}))
            stack.enter_context(patch.object(service, 'CurrentBuildBackend', return_value=backend))
            with self.assertRaisesRegex(ValueError, 'extents differ'):
                self.runtime.prepare(1, self.temp.name, self.runtime.check, 1)
        backend.close.assert_called_once()

    def test_reader_constructor_failure_closes_opened_handle(self):
        readers = []
        def missing_modules(reader, pid):
            reader.h = 123
            readers.append(reader)
            raise ValueError('GameAssembly module not found')
        with patch.object(native_provider_backend.Reader, '__init__', missing_modules), \
                patch.object(native_provider_backend.K, 'CloseHandle') as close:
            with self.assertRaisesRegex(ValueError, 'module not found'):
                native_provider_backend.GuardedReader(1)
        close.assert_called_once_with(123)
        self.assertIsNone(readers[0].h)

    def test_original_io_receives_proxy_central_stop_and_current_phase_budget(self):
        self.resolve()
        io = SimpleNamespace(deadline=time.monotonic()+4, accepted_session_deadline=time.monotonic()+10)
        with patch.object(project_closed_loop_io, 'ProjectClosedLoopIO', return_value=io) as factory:
            self.assertIs(self.runtime.create_io(None, {}, self.temp.name, stop=self.stop), io)
        self.assertIs(factory.call_args.kwargs['game'], self.game)
        self.assertIs(factory.call_args.kwargs['stop'], self.stop)
        self.assertFalse(factory.call_args.kwargs['f9_pressed']())
        self.game.perform_gesture(PointerGesture('drag', ((20, 20), (30, 20))))
        first = [args['remainingSeconds'] for name,args in self.port.calls if name=='game.gesture'][-1]
        io.deadline = time.monotonic()+1
        self.game.perform_gesture(PointerGesture('drag', ((20, 20), (30, 20))))
        second = [args['remainingSeconds'] for name,args in self.port.calls if name=='game.gesture'][-1]
        self.assertTrue(3 < first <= 4)
        self.assertTrue(0 < second <= 1)
        probe = object.__new__(ProjectProbeIO)
        probe.game, probe.sent_any = self.game, True
        probe.release()
        self.assertEqual(self.port.calls[-1], ('game.release', {}))

    def test_original_retry_route_keeps_all_wheel_steps_and_waits(self):
        board = (10, 10, 190, 110)
        route = [PointerGesture('wheel', ((100, 60),), wheel_steps=-32).record() for _ in range(64)]
        gesture = project_closed_loop_io.validate_retry_reset_route(route, board)
        self.assertEqual(gesture.wheel_steps, -2048)
        recorder, waits = OriginalRecorder(), []
        from mabitool_color._vendor import platform_win
        with patch.object(platform_win.time, 'sleep', side_effect=waits.append):
            platform_win.Game._perform_gesture(recorder, gesture)
        self.game.perform_gesture(gesture)
        sent = [args['gesture'] for name,args in self.port.calls if name=='game.gesture'][-1]
        self.assertEqual(sent['wheelSteps'], -2048)
        self.assertEqual(recorder.trace, ['absolute:200,260']+['wheel:-120']*2048)
        self.assertEqual(waits, [sent['timing']['pointInterval']]*2048+[sent['timing']['afterWheel']])

    def test_original_closed_loop_releases_through_typed_proxy_after_action(self):
        io = object.__new__(project_closed_loop_io.ProjectClosedLoopIO)
        observation = dict(active=True, motion=dict(pose={}, animators_done=True), geometry_diagnostic={},
            window_mapping_candidate=dict(client_board_candidate=(10, 10, 190, 110)))
        io.backend = SimpleNamespace(adapter=TW_ADAPTER, observe_motion=lambda *args,**kw: observation)
        io.last_observation, io.reference = observation, dict(instance_address=1)
        io.folder, io.input_attempts = Path(self.temp.name), 0
        io.game = self.game
        io.check = lambda deadline: setattr(io, 'deadline', deadline)
        io.input_backend = lambda deadline: {}
        io._input_guard = self.game.check
        io._check_observed_session = lambda state,*args,**kw: state
        record = PointerGesture('drag', ((90, 60), (92, 60))).record()
        with patch.object(project_closed_loop_io, '_binding', return_value='bound'):
            receipt = io.perform_candidate(record, 'offline_action', time.monotonic()+30)
        self.assertTrue(receipt['completed'])
        self.assertTrue(receipt['released'])
        self.assertEqual(receipt['input_source'], 'host_game_input_proxy')
        self.assertEqual(self.port.calls[-1], ('game.release', {}))
        self.assertEqual(io.input_attempts, 1)

    def test_screenshot_worker_does_not_load_process_reader(self):
        code = '''
from pathlib import Path
import sys, tempfile, threading
sys.path.insert(0, str(Path('workers/color-engine').resolve()))
sys.path.insert(0, str(Path('workers/color-engine/tests').resolve()))
from mabitool_color.worker import Worker
from mabitool_color._vendor.profile_store import default_profile
from test_game_proxy import screenshot_scene, OfflinePort
with tempfile.TemporaryDirectory() as root, screenshot_scene(2):
    port = OfflinePort(root)
    worker = Worker(None, '', root)
    worker.invoke_port = lambda frame,cancel,method,args: port(method,args)
    frame = dict(requestId='b'*32, runId=port.run, bindingRevision='1', fencingToken='1', payload=dict(method='color.screenshot', arguments=dict(profile=default_profile())))
    worker.execute(frame, threading.Event())
    import json
    result = [json.loads(data[4:]) for data in worker.outgoing.queue][-1]
    assert result['kind']=='result' and result['payload']['accepted'], result
    assert 'mabitool_color._vendor.native_live.runtime_read' not in sys.modules
    assert not any(method=='game.nativeAccess' for method,args in port.calls)
'''
        result = subprocess.run([sys.executable, '-B', '-c', code], cwd=Path(__file__).resolve().parents[3],
                                text=True, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)


if __name__ == '__main__':
    unittest.main()
