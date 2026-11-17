"""无界面的单次染色运行；外部端口负责绑定、采集及输入授权。"""
from __future__ import annotations

import copy
from pathlib import Path
import threading
import time
from typing import Callable
from uuid import uuid4

from ._vendor.profile_store import canonical_profile
from ._vendor.run_stop import StopEvent
from .measurements import annotate, candidate_fields, reading_fields, region_states
from .ports import NativeRuntime


class ColorEngineContext:
    """保留启动快照与上游 owner 协议，每个实例只运行一次。"""

    def __init__(self, profile: dict, binding: dict, folder: str | Path,
                 emit: Callable[[dict], None], *, run_id: str | None = None):
        self._profile = canonical_profile(copy.deepcopy(profile), strict=True)
        order = self._profile['priority_order']
        self._rules = []
        for index, row in enumerate(self._profile['regions']):
            self._rules.append(dict(
                enabled=row['enabled'],
                colors=([row['target']] + [s.strip() for s in row['alt'].split(',') if s.strip()]
                        if row['enabled'] else []),
                exact=row['mode'] == '精准 HEX' if row['enabled'] else True,
                tolerance=row['tolerance'] if row['enabled'] else 0,
                priority=order.index(index) + 1))
        self._binding = copy.deepcopy(binding)
        self.folder = Path(folder)
        self.run_id = run_id or uuid4().hex
        self.stop = StopEvent()
        self._emit = emit
        self._lock = threading.RLock()
        self._state = 'created'
        self._sequence = 0
        self._retry = False
        self._interaction_revision = 0
        self._candidate_batch = None
        self._candidates = set()
        self._candidate_deadline = 0.
        self._choice = None
        self._retry_enabled = False
        self._retry_deadline = None
        self._last_stage = None

    def _interaction(self):
        active = self._state == 'running' and not self.stop.is_set()
        return dict(revision=str(self._interaction_revision), batchId=self._candidate_batch,
                    selectionEnabled=active and not self._retry and bool(self._candidates) and time.monotonic() < self._candidate_deadline,
                    retryEnabled=active and self._retry_enabled and not self._retry and
                        (self._retry_deadline is None or time.monotonic() < self._retry_deadline))

    @property
    def interaction(self):
        with self._lock:
            return self._interaction()

    def interact(self, action, revision, *, batch_id=None, candidate_id=None):
        """受理本轮命令；实际动作仍由原控制器在检查点执行。"""
        with self._lock:
            code = None
            if self._state != 'running' or self.stop.is_set():
                code = 'color.runInactive'
            elif revision != str(self._interaction_revision):
                code = 'color.interactionConflict'
            elif action == 'select':
                if (self._retry or batch_id != self._candidate_batch or candidate_id not in self._candidates or
                        time.monotonic() >= self._candidate_deadline):
                    code = 'color.candidateUnavailable'
                else:
                    self._choice = (batch_id, candidate_id)
                    self._candidates.clear()
            elif action == 'retry':
                if not self._interaction()['retryEnabled']:
                    code = 'color.retryUnavailable'
                else:
                    self._retry = True
                    self._choice = None
                    self._candidate_batch = None
                    self._candidates.clear()
            else:
                code = 'worker.commandInvalid'
            if code is None:
                self._interaction_revision += 1
            return dict(accepted=code is None, code=code, interaction=self._interaction())

    def consume_candidate_choice(self, batch_id, poll_deadline):
        with self._lock:
            if self.stop.is_set() or time.monotonic() >= min(poll_deadline, self._candidate_deadline):
                self._choice = None
                return None
            choice, self._choice = self._choice, None
            return choice[1] if choice is not None and choice[0] == batch_id else None

    def _close_interaction(self):
        self._retry = False
        self._retry_enabled = False
        self._choice = None
        self._candidate_batch = None
        self._candidates.clear()
        self._interaction_revision += 1

    @property
    def rules(self):
        return copy.deepcopy(self._rules)

    @property
    def binding(self):
        return copy.deepcopy(self._binding)

    @property
    def profile(self):
        return copy.deepcopy(self._profile)

    @property
    def state(self):
        with self._lock:
            return self._state

    def event(self, kind, **data):
        with self._lock:
            if kind in ('native_retry_available', 'native_retry_reset', 'native_candidate_ready',
                        'native_candidate_selected', 'native_candidate_closed'):
                if kind == 'native_retry_available':
                    self._retry_enabled = data.get('enabled') is True
                    self._retry_deadline = data.get('effective_deadline')
                elif kind == 'native_retry_reset':
                    self._retry_enabled = False
                    self._choice = None
                    self._candidate_batch = None
                    self._candidates.clear()
                elif kind == 'native_candidate_ready':
                    rules = self._rules[:data.get('region_count', 0)]
                    data['candidates'] = [dict(row, **candidate_fields(row, rules)) for row in data['candidates']]
                    self._candidate_batch = data['batch_id']
                    self._candidate_deadline = min(data['effective_deadline'], data.get('selection_deadline', data['effective_deadline']))
                    self._candidates = {row['id'] for row in data['candidates'] if row.get('available') is True}
                elif kind == 'native_candidate_closed':
                    self._candidate_batch = None
                    self._candidates.clear()
                    self._choice = None
                self._interaction_revision += 1
                data['interaction'] = self._interaction()
            if kind == 'native_progress' and isinstance(data.get('stage'), str):
                self._last_stage = data['stage']
            if kind in ('scene', 'native_colors'):
                data.update(reading_fields(data.get('colors'), self._rules))
            elif isinstance(data.get('current_colors'), list) and isinstance(data.get('current_deltas'), list):
                rules = self._rules[:len(data['current_colors'])]
                if len(data['current_deltas']) == len(rules):
                    data['current_region_states'] = region_states(data['current_colors'], data['current_deltas'], rules)
            if kind in ('screenshot_result', 'native_result'):
                annotate(data)
            elif kind == 'finished':
                annotate(data.get('result'))
            self._sequence += 1
            self._emit(dict(run_id=self.run_id, sequence=self._sequence,
                            at_monotonic=time.monotonic(), kind=kind,
                            data=copy.deepcopy(data)))

    def _preparation_stage(self):
        """精准寻色没有结果时停在的步骤：准备未完成时取准备记录的步骤，否则取最后一个进度阶段。"""
        try:
            import json
            stage = json.loads((self.folder / 'native-preparation.json').read_text(encoding='utf8')).get('stage')
        except (OSError, ValueError, AttributeError):
            stage = None
        if stage == 'complete' or not isinstance(stage, str):
            stage = self._last_stage
        return stage if isinstance(stage, str) and 1 <= len(stage) <= 64 else None

    def request_stop(self, source='user_stop'):
        self.stop.request(source)
        with self._lock:
            self._close_interaction()

    def request_retry(self):
        with self._lock:
            if self._state != 'running' or self.stop.is_set():
                return False
            self._retry = True
            return True

    def consume_retry_request(self):
        with self._lock:
            pending = self._retry and not self.stop.is_set()
            self._retry = False
            return pending

    def run(self, *, screenshot_game_factory=None, native_runtime: NativeRuntime | None = None,
            wait_for_retry=True):
        strategy = self._profile['search_strategy']
        if strategy == 'screenshot' and screenshot_game_factory is None:
            raise ValueError('截图策略需要显式提供游戏代理。')
        if strategy == 'native' and native_runtime is None:
            raise ValueError('精准策略需要显式提供读取与输入代理。')
        with self._lock:
            if self._state != 'created':
                raise RuntimeError('每个上下文只允许启动一次。')
            self._state = 'running'
        try:
            self.folder.mkdir(parents=True, exist_ok=True)
            self.event('started', strategy=strategy, rules=self.rules, binding=self.binding)
            if self.stop.is_set():
                result = dict(stop_reason='interrupted', verified=False, accepted=False)
            elif strategy == 'screenshot':
                from ._vendor.screenshot_live import run_screenshot_search
                from ._vendor.platform_win import Interrupted
                try:
                    result = run_screenshot_search(
                        self, self.rules, target=self.binding.get('target'),
                        server=self.binding.get('server'), game_factory=screenshot_game_factory,
                        wait_for_retry=wait_for_retry)
                except Interrupted as exc:
                    if self.stop.is_set() or exc.reason == 'user_stop':
                        raise InterruptedError(str(exc)) from exc
                    raise
            else:
                from ._vendor.native_live.service import run_native_search
                result = run_native_search(
                    self, self.rules, target=self.binding.get('target'),
                    server=self.binding.get('server'), runtime=native_runtime)
                if result.get('stop_reason') != 'interrupted' and not result.get('verified'):
                    result['preparation_stage'] = self._preparation_stage()
            with self._lock:
                self._state = 'stopped' if self.stop.is_set() or result.get('stop_reason') == 'interrupted' else 'completed'
            self.event('finished', state=self.state, result=result)
            return copy.deepcopy(result)
        except InterruptedError:
            with self._lock:
                self._state = 'stopped'
            result = dict(stop_reason='interrupted', verified=False, accepted=False)
            self.event('finished', state=self.state, result=result)
            return result
        except BaseException as exc:
            self.request_stop('engine_failed')
            with self._lock:
                self._state = 'failed'
            self.event('failed', error_type=type(exc).__name__, message=str(exc))
            raise
        finally:
            with self._lock:
                self._close_interaction()
