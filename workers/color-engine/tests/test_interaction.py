"""本轮候选、修订和重试受理；只使用离线引擎端口。"""
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mabitool_color import ColorEngineContext
from mabitool_color._vendor.profile_store import default_profile
from mabitool_color._vendor.native_live import service
from test_context import NativeReplay
from test_context import session
from test_context import ReplayGame
from test_game_proxy import screenshot_scene
from dataclasses import asdict
import copy
import json
import io
from mabitool_color._vendor.native_input_response import InputSettings
from mabitool_color._vendor.native_live.controller import run_goal_loop
from mabitool_color._vendor.screenshot_search import actual_result
from mabitool_color._vendor.native_live.compromise import observed_quality
from mabitool_color.worker import Worker
from mabitool_color import protocol


class InteractionTests(unittest.TestCase):
    def test_rejected_output_does_not_consume_wire_sequence(self):
        worker = Worker(None, '', '')
        with self.assertRaises(ValueError):
            worker.send('pong', dict(invalid=float('inf')))
        worker.send('pong')
        frame = protocol.read(io.BytesIO(worker.outgoing.get_nowait()))
        self.assertEqual(frame['messageId'], '1')

    def run_driver(self, driver):
        with tempfile.TemporaryDirectory() as folder:
            context = ColorEngineContext(default_profile(), {}, folder, lambda row: None)
            with patch('mabitool_color._vendor.screenshot_live.run_screenshot_search',
                       side_effect=lambda owner, *args, **kwargs: driver(owner)):
                context.run(screenshot_game_factory=lambda *args: None)
            return context

    def publish(self, c, batch='board-1', deadline=None):
        c.event('native_candidate_ready', batch_id=batch,
                effective_deadline=deadline if deadline is not None else time.monotonic() + 30,
                candidates=[dict(id='current', available=False), dict(id='choice', available=True)])

    def command(self, c, action='select', **kwargs):
        args = dict(batch_id='board-1', candidate_id='choice') if action == 'select' else {}
        args.update(kwargs)
        return c.interact(action, c.interaction['revision'], **args)

    def test_selection_is_consumed_once_and_duplicate_revision_conflicts(self):
        def driver(c):
            self.publish(c)
            revision = c.interaction['revision']
            accepted = self.command(c)
            self.assertTrue(accepted['accepted'])
            self.assertFalse(accepted['interaction']['selectionEnabled'])
            self.assertEqual(c.interact('select', revision, batch_id='board-1', candidate_id='choice')['code'],
                             'color.interactionConflict')
            self.assertEqual(c.consume_candidate_choice('board-1', time.monotonic() + 1), 'choice')
            self.assertIsNone(c.consume_candidate_choice('board-1', time.monotonic() + 1))
            return {}
        c = self.run_driver(driver)
        self.assertEqual(self.command(c)['code'], 'color.runInactive')

    def test_expired_wrong_batch_unknown_and_current_candidates_do_not_queue(self):
        def driver(c):
            self.publish(c)
            for change in (dict(batch_id='old'), dict(candidate_id='unknown'), dict(candidate_id='current')):
                self.assertEqual(self.command(c, **change)['code'], 'color.candidateUnavailable')
            self.publish(c, deadline=time.monotonic() - 1)
            self.assertFalse(c.interaction['selectionEnabled'])
            self.assertEqual(self.command(c)['code'], 'color.candidateUnavailable')
            self.assertIsNone(c.consume_candidate_choice('board-1', time.monotonic() + 1))
            return {}
        self.run_driver(driver)

    def test_retry_invalidates_pending_choice_and_keeps_original_deadline(self):
        def driver(c):
            end = time.monotonic() + 20
            self.publish(c, deadline=end)
            self.assertTrue(self.command(c)['accepted'])
            c.event('native_retry_available', enabled=True, effective_deadline=end)
            self.assertTrue(self.command(c, 'retry')['accepted'])
            self.assertIsNone(c.consume_candidate_choice('board-1', end))
            self.assertIsNone(c.interaction['batchId'])
            self.assertFalse(c.interaction['retryEnabled'])
            self.assertEqual(self.command(c, 'retry')['code'], 'color.retryUnavailable')
            self.assertTrue(c.consume_retry_request())
            self.assertFalse(c.consume_retry_request())
            c.event('native_retry_reset', status='searching', effective_deadline=end)
            self.publish(c, batch='board-2', deadline=end)
            self.assertEqual(self.command(c)['code'], 'color.candidateUnavailable')
            self.assertEqual(c._candidate_deadline, end)
            self.assertTrue(self.command(c, batch_id='board-2')['accepted'])
            c.request_stop()
            self.assertEqual(self.command(c, 'retry')['code'], 'color.runInactive')
            self.assertIsNone(c.consume_candidate_choice('board-2', end))
            return {}
        self.run_driver(driver)

    def test_closed_batch_and_unavailable_retry_are_rejected(self):
        def driver(c):
            self.publish(c)
            c.event('native_candidate_closed', batch_id='board-1', reason='deadline')
            self.assertEqual(self.command(c)['code'], 'color.candidateUnavailable')
            self.assertEqual(self.command(c, 'retry')['code'], 'color.retryUnavailable')
            c.event('native_retry_available', enabled=True, effective_deadline=time.monotonic() - 1)
            self.assertEqual(self.command(c, 'retry')['code'], 'color.retryUnavailable')
            return {}
        self.run_driver(driver)

    def test_native_service_forwards_candidates_and_both_controller_callbacks(self):
        with tempfile.TemporaryDirectory() as folder:
            events = []
            c = ColorEngineContext(dict(default_profile(), search_strategy='native'), {}, folder, events.append)
            def controller(*args, **kwargs):
                self.assertTrue(callable(kwargs['candidate_choice']))
                self.assertTrue(callable(kwargs['retry_requested']))
                kwargs['event'](dict(event='native_candidate_ready', batch_id='board-1',
                    effective_deadline=time.monotonic()+30, candidates=[dict(id='choice', available=True)]))
                self.assertTrue(self.command(c)['accepted'])
                self.assertEqual(kwargs['candidate_choice']('board-1', time.monotonic()+1), 'choice')
                kwargs['event'](dict(event='native_candidate_closed', batch_id='board-1', reason='deadline'))
                return dict(stop_reason='insufficient_time')
            with patch.object(service, 'run_goal_loop', side_effect=controller):
                c.run(native_runtime=NativeReplay(2))
            self.assertIn('native_candidate_ready', [e['kind'] for e in events])
            self.assertFalse(c.interaction['selectionEnabled'])

    def test_original_native_controller_retries_while_choosing_without_resetting_budget(self):
        settings = InputSettings(1., 10., 0., 0., .01)
        for count in (2, 3):
            with self.subTest(count=count):
                now = [1000.]
                captured = session(count)
                checkpoint = dict(checkpoint_valid=True, cpu_matches=True, board=[0, 0, 200, 100],
                    local_size=[200, 100], pose=captured['initial_pose'], client_hex=['#202020'] * count,
                    binding=json.dumps(['offline', 'process-1', {}, asdict(settings), {}]))
                class IO:
                    input_attempts = 0
                    released = 0
                    def check(self, until):
                        if now[0] >= until: raise TimeoutError('offline deadline')
                    def input_backend(self, until):
                        return dict(process_identity='process-1', viewport_origin=[0, 0], viewport_scale=[1, 1])
                    def checkpoint(self, label, until):
                        return copy.deepcopy(checkpoint)
                    def frames(self, label, until):
                        rows = []
                        for _ in range(2):
                            now[0] += .01
                            rows.append(dict(hex=['#202020'] * count, captured_monotonic=now[0], remaining_seconds=100))
                        now[0] += .01
                        return rows
                    def release(self): self.released += 1
                io = IO()
                pending = [False]
                requested = [False]
                batches = []
                def choose(batch, poll_end):
                    batches.append(batch)
                    if not requested[0]:
                        requested[0] = pending[0] = True
                    else:
                        now[0] = 1088.
                    return None
                def retry():
                    value, pending[0] = pending[0], False
                    return value
                events = []
                def pause(seconds): now[0] += seconds
                # 低精度 Windows 时钟可能连续返回相同纳秒值，批次仍须轮换。
                with patch('mabitool_color._vendor.native_live.controller.time.monotonic_ns', return_value=123):
                    result = run_goal_loop(io, captured, settings,
                        [dict(enabled=True, colors=['#202020'], exact=True, tolerance=0)] * count,
                        engineering_deadline=1100., clock=lambda: now[0], pause=pause,
                        planner=lambda *args, **kwargs: dict(candidate_pool=[]),
                        candidate_choice=choose, retry_requested=retry, event=events.append)
                self.assertEqual(result['retry_epochs'], 1, result.get('error'))
                self.assertGreaterEqual(len(set(batches)), 2, dict(batches=batches,
                    ready=[e.get('batch_id') for e in events if e.get('kind') == 'native_candidate_ready'],
                    result=result.get('stop_reason')))
                self.assertEqual(result['actual_input_attempts'], 0)
                self.assertTrue(result['verified'], result.get('error'))
                self.assertEqual(result['actual_colors'], ['#202020'] * count)
                self.assertTrue(all(e['effective_deadline'] == 1100. for e in events if 'effective_deadline' in e))
                self.assertGreaterEqual(io.released, 1)

    def test_screenshot_stop_during_wait_preserves_history_without_claiming_current(self):
        with tempfile.TemporaryDirectory() as folder, screenshot_scene(2):
            profile = default_profile()
            for row in profile['regions']: row.update(target='#202020', alt='')
            def emit(row):
                if row['kind'] == 'native_retry_available' and row['data'].get('waiting_for_retry'):
                    context.request_stop()
            context = ColorEngineContext(profile, {}, folder, emit)
            def factory(*args, **kwargs):
                game = ReplayGame(*args, **kwargs)
                game.central_stop = True
                return game
            result = context.run(screenshot_game_factory=factory, wait_for_retry=True)
            self.assertFalse(result['verified'])
            self.assertIsNone(result['measurements']['currentVerified'])
            self.assertEqual(result['measurements']['lastMeasured']['colors'], ['#202020'] * 2)
            self.assertEqual(result['measurements']['bestMeasured']['colors'], ['#202020'] * 2)
            self.assertTrue(result['measurements']['lastMeasured']['targetExact'])
            json.dumps(result, allow_nan=False)

    def test_compromise_priority_order_is_shared_by_original_strategies(self):
        with tempfile.TemporaryDirectory() as folder:
            for order in ([0, 1, 2], [1, 0, 2]):
                profile = default_profile()
                profile['priority_order'] = order
                for row in profile['regions']: row.update(target='#202020', alt='', mode='精准 HEX')
                c = ColorEngineContext(profile, {}, folder, lambda row: None)
                for count in (2, 3):
                    rules = c.rules[:count]
                    first = ['#202020', '#FFFFFF', '#202020'][:count]
                    second = ['#FFFFFF', '#202020', '#202020'][:count]
                    prefer_first = order[0] == 0
                    self.assertEqual(actual_result(first, rules)['rank'] < actual_result(second, rules)['rank'], prefer_first)
                    self.assertEqual(observed_quality(first, rules) < observed_quality(second, rules), prefer_first)


if __name__ == '__main__':
    unittest.main()
