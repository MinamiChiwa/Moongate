"""本机染色 Worker：控制读取与计算独立，心跳不依赖算法完成。"""
import json
import os
from pathlib import Path
import queue
import sys
import threading
import time

from . import protocol as wire


class Worker:
    def __init__(self, stream, token, asset_root, *, heartbeat_timeout=5.):
        self.stream, self.token, self.asset_root = stream, token, asset_root
        self.timeout = heartbeat_timeout
        self.last_contact = time.monotonic()
        self.stopped = threading.Event()
        self.outgoing = queue.Queue(maxsize=64)
        self.lock = threading.RLock()
        self.incoming_id = self.outgoing_id = 0
        self.active = None
        self.seen = set()
        self.port_sequence = 0
        self.port_pending = None
        self.current_context = None
        self.command_sequence = 0

    def command(self, frame):
        with self.lock:
            active = self.active
            if active is None:
                return  # 终态已发送，宿主依据终态结束命令等待。
            if any(active[0][key] != frame[key] for key in ('requestId', 'runId', 'bindingRevision', 'fencingToken')):
                raise wire.ProtocolError('worker.requestIdentityInvalid')
            payload = frame['payload']
            action = payload.get('action')
            required = {'commandId', 'action', 'revision'}
            if action == 'select': required |= {'batchId', 'candidateId'}
            if (action not in ('select', 'retry') or set(payload) != required or
                    payload.get('commandId') != str(self.command_sequence + 1) or
                    not wire.uint64(payload.get('revision')) or
                    any(not isinstance(payload[key], str) or not 1 <= len(payload[key]) <= 256
                        for key in ('batchId', 'candidateId') if key in required)):
                raise wire.ProtocolError('worker.commandInvalid')
            self.command_sequence += 1
            context = self.current_context
        # 不持有传输锁调用上下文，事件回调使用相反的锁顺序。
        result = (context.interact(action, payload['revision'], batch_id=payload.get('batchId'),
                  candidate_id=payload.get('candidateId')) if context is not None and not active[1].is_set()
                  else dict(accepted=False, code='color.runInactive', interaction=None))
        with self.lock:
            if self.active is active:
                self.send('commandResult', dict(commandId=payload['commandId'], **result), frame)

    def invoke_port(self, frame, cancel, method, arguments):
        with self.lock:
            if cancel.is_set(): raise InterruptedError()
            if self.port_pending is not None: raise wire.ProtocolError('worker.portInvalid')
            self.port_sequence += 1
            pending = dict(call_id=str(self.port_sequence), ready=threading.Event(), response=None, frame=frame)
            self.port_pending = pending
            self.send('portRequest', dict(callId=pending['call_id'], method=method, arguments=arguments), frame)
        try:
            while not pending['ready'].wait(.05):
                if cancel.is_set() or self.stopped.is_set(): raise InterruptedError()
            response = pending['response']
            if response['kind'] == 'portError': raise wire.ProtocolError(response['payload']['code'])
            return response['payload']['result']
        finally:
            with self.lock:
                if self.port_pending is pending: self.port_pending = None

    def send(self, kind, payload=None, scope=None):
        with self.lock:
            next_id = self.outgoing_id + 1
            frame = dict(protocolVersion=1, messageId=str(next_id), kind=kind,
                         payload=payload or {}, **{key: scope[key] if scope else None
                         for key in ('requestId', 'runId', 'bindingRevision', 'fencingToken')})
            data = wire.encode(frame)
            try: self.outgoing.put_nowait(data)
            except queue.Full: raise wire.ProtocolError('worker.outputBackpressure')
            self.outgoing_id = next_id

    def write_loop(self):
        try:
            while not self.stopped.is_set():
                try: data = self.outgoing.get(timeout=.1)
                except queue.Empty: continue
                while data:
                    count = self.stream.write(data)
                    if not count: raise EOFError()
                    data = data[count:]
                self.stream.flush()
                self.outgoing.task_done()
        except BaseException:
            self.stopped.set()
            os._exit(3)

    def watch_loop(self):
        while not self.stopped.wait(.1):
            if time.monotonic() - self.last_contact > self.timeout:
                if self.active: self.active[1].set()
                os._exit(4)

    def execute(self, frame, cancel):
        from ._vendor.platform_win import Interrupted
        context = None
        try:
            args = frame['payload']
            if set(args) not in ({'method', 'arguments'}, {'method', 'argumentsAsset'}):
                raise wire.ProtocolError('worker.requestInvalid')
            values = args.get('arguments')
            if 'argumentsAsset' in args:
                values = json.loads(wire.read_asset(self.asset_root, args['argumentsAsset'], frame['runId']).decode('utf-8'),
                                    object_pairs_hook=wire.unique, parse_constant=wire.invalid_constant)
            method = args['method']
            if method not in ('color.evaluate', 'color.screenshot', 'color.native', 'color.palette'):
                raise wire.ProtocolError('worker.methodUnavailable')
            if method == 'color.palette':
                # 目标色集合只做纯计算，不创建运行上下文，也不请求游戏端口。
                from .palette_render import render
                import cv2
                cv2.setNumThreads(int(os.environ.get('MABITOOL_COLOR_THREADS', '1')))
                if cancel.is_set(): raise InterruptedError()
                result = render(self.asset_root, frame['runId'], values, cancel)
                if cancel.is_set(): raise InterruptedError()
                with self.lock:
                    self.send('result', result, frame)
                    self.active = None
                return
            fields = set(values) if isinstance(values, dict) else set()
            if (not isinstance(values, dict) or
                    (fields != {'profile', 'colors'} if method == 'color.evaluate' else fields not in ({'profile'}, {'profile', 'interactive'})) or
                    ('interactive' in values and type(values['interactive']) is not bool)):
                raise wire.ProtocolError('worker.requestInvalid')
            if cancel.is_set(): raise InterruptedError()
            import cv2
            cv2.setNumThreads(int(os.environ.get('MABITOOL_COLOR_THREADS', '1')))
            from .context import ColorEngineContext
            from ._vendor.dye_regions import bind_region_rules
            from ._vendor.screenshot_search import actual_result
            context = ColorEngineContext(values['profile'], {}, Path(self.asset_root) / frame['runId'],
                                         lambda row: self.send('event', row, frame),
                                         run_id=frame['runId'])
            with self.lock:
                self.current_context = context
                if cancel.is_set(): context.request_stop('host_cancel')
            if method == 'color.evaluate':
                colors = values['colors']
                if not isinstance(colors, list) or len(colors) not in (2, 3):
                    raise wire.ProtocolError('worker.requestInvalid')
                self.send('event', dict(phase='evaluate', rules=context.rules), frame)
                rules = bind_region_rules(context.rules, len(colors))
                result = dict(result=actual_result(colors, rules), rules=rules, regionCount=len(colors))
                result['result'].pop('rank', None)
            else:
                strategy = 'native' if method == 'color.native' else 'screenshot'
                if context.profile['search_strategy'] != strategy: raise wire.ProtocolError('worker.requestInvalid')
                from .game_proxy import ProxyGame
                factory = lambda stop, target=None: ProxyGame(
                    stop, lambda name, arguments: self.invoke_port(frame, cancel, name, arguments),
                    self.asset_root, frame['runId'])
                if method == 'color.native':
                    from .native_runtime import ProxyNativeRuntime
                    result = context.run(native_runtime=ProxyNativeRuntime(factory(context.stop)))
                else:
                    result = context.run(screenshot_game_factory=factory, wait_for_retry=values.get('interactive', False))
            if cancel.is_set(): raise InterruptedError()
            with self.lock:
                self.send('result', result, frame)
                self.active = None
        except InterruptedError:
            with self.lock:
                self.send('cancelled', {}, frame)
                self.active = None
        except Interrupted as exc:
            code = {'binding_changed': 'input.bindingChanged', 'focus_lost': 'input.focusChanged',
                    'deadline_exceeded': 'input.deadlineExceeded', 'user_stop': 'input.runStopped'}.get(
                        exc.reason, 'worker.portFailed')
            with self.lock:
                self.send('error', dict(code=code), frame)
                self.active = None
        except (ValueError, TypeError, KeyError, OSError) as exc:
            code = str(exc) if isinstance(exc, wire.ProtocolError) else 'worker.requestInvalid'
            with self.lock:
                try: self.send('error', dict(code=code), frame)
                except BaseException: os._exit(5)
                self.active = None
        except BaseException:
            # 终态无法发送时退出，让宿主依据断连清理运行权限。
            os._exit(5)
        finally:
            with self.lock:
                if self.current_context is context: self.current_context = None

    def run(self):
        threading.Thread(target=self.write_loop, daemon=True).start()
        threading.Thread(target=self.watch_loop, daemon=True).start()
        self.send('hello', dict(workerId='color-engine', minimumVersion=1, maximumVersion=1,
                                token=self.token, pid=os.getpid()))
        ready = False
        try:
            while not self.stopped.is_set():
                frame = wire.read(self.stream)
                if int(frame['messageId']) != self.incoming_id + 1:
                    raise wire.ProtocolError('worker.sequenceInvalid')
                self.incoming_id += 1
                kind = frame['kind']
                if not ready:
                    if kind != 'ready' or frame['payload'] != {'workerId': 'color-engine'}:
                        raise wire.ProtocolError('worker.handshakeInvalid')
                    ready = True
                    self.last_contact = time.monotonic()
                    continue
                if kind == 'ping':
                    if frame['payload']: raise wire.ProtocolError('worker.frameInvalid')
                    self.last_contact = time.monotonic()
                    self.send('pong')
                elif kind == 'request':
                    with self.lock:
                        if self.active is not None:
                            self.send('error', dict(code='worker.busy'), frame)
                            continue
                        if frame['requestId'] in self.seen: raise wire.ProtocolError('worker.requestReplayed')
                        if len(self.seen) >= 4096: raise wire.ProtocolError('worker.sessionLimit')
                        self.seen.add(frame['requestId'])
                        self.port_sequence = 0
                        self.command_sequence = 0
                        cancel = threading.Event()
                        self.active = (frame, cancel)
                        self.send('accepted', {}, frame)
                        threading.Thread(target=self.execute, args=(frame, cancel), daemon=True).start()
                elif kind == 'cancel':
                    context = None
                    with self.lock:
                        if self.active:
                            if any(self.active[0][k] != frame[k] for k in ('requestId', 'runId', 'bindingRevision', 'fencingToken')):
                                raise wire.ProtocolError('worker.requestIdentityInvalid')
                            self.active[1].set()
                            context = self.current_context
                    if context is not None: context.request_stop('host_cancel')
                elif kind == 'command':
                    self.command(frame)
                elif kind in ('portResult', 'portError'):
                    with self.lock:
                        pending = self.port_pending
                        if pending is None:
                            # 取消已结束本次端口等待，迟到回复不能恢复它。
                            if self.active is None or self.active[1].is_set(): continue
                            raise wire.ProtocolError('worker.portInvalid')
                        expected = pending['frame']
                        if any(expected[k] != frame[k] for k in ('requestId', 'runId', 'bindingRevision', 'fencingToken')):
                            raise wire.ProtocolError('worker.requestIdentityInvalid')
                        payload = frame['payload']
                        if (set(payload) != {'callId', 'result' if kind == 'portResult' else 'code'} or
                                payload['callId'] != pending['call_id'] or pending['ready'].is_set()):
                            raise wire.ProtocolError('worker.portInvalid')
                        pending['response'] = frame
                        pending['ready'].set()
                elif kind == 'shutdown':
                    if self.active: self.active[1].set()
                    self.send('bye')
                    self.outgoing.join()
                    return
                else: raise wire.ProtocolError('worker.frameInvalid')
        finally:
            if self.active: self.active[1].set()
            self.stopped.set()


def main():
    pipe = os.environ.pop('MABITOOL_WORKER_PIPE', '')
    token = os.environ.pop('MABITOOL_WORKER_TOKEN', '')
    root = os.environ.pop('MABITOOL_WORKER_ASSET_ROOT', '')
    prefix = next((item for item in ('mabitool-worker-', 'mabitool-color-') if pipe.startswith(item)), '')
    if not prefix or not wire.identifier(pipe[len(prefix):]) or len(token) != 64 or not Path(root).is_absolute():
        return 2
    try:
        with wire.PipeStream(pipe) as stream:
            Worker(stream, token, root).run()
        return 0
    except (OSError, EOFError, ValueError): return 3


if __name__ == '__main__':
    sys.exit(main())
