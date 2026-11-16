"""仅供跨进程测试的故障对端，生产入口不导入本模块。"""
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mabitool_color import protocol as p


def main():
    mode = sys.argv[1]
    if mode == 'wrong-pid':
        subprocess.run([sys.executable, '-B', '-s', '-m', 'fault_peer', 'wrong-pid-child'], check=False)
        return
    pipe = os.environ['MABITOOL_WORKER_PIPE']
    token = os.environ['MABITOOL_WORKER_TOKEN']
    sequence = 0
    with open('\\\\.\\pipe\\' + pipe, 'r+b', buffering=0) as stream:
        def send(kind, payload=None, scope=None):
            nonlocal sequence
            sequence += 1
            frame = dict(protocolVersion=1, messageId=str(sequence), kind=kind, payload=payload or {},
                         **{key: scope[key] if scope else None for key in ('requestId', 'runId', 'bindingRevision', 'fencingToken')})
            stream.write(p.encode(frame))
            return frame
        send('hello', dict(workerId='color-engine', minimumVersion=2 if mode == 'wrong-version' else 1,
                          maximumVersion=2 if mode == 'wrong-version' else 1,
                          token='0' * 64 if mode == 'wrong-token' else token, pid=os.getpid()))
        p.read(stream)
        if mode == 'silent': time.sleep(30); return
        active = None
        while True:
            frame = p.read(stream)
            if frame['kind'] == 'ping': send('pong'); continue
            if frame['kind'] == 'request':
                active = frame
                send('accepted', {}, frame)
                if mode == 'crash': os._exit(7)
                if mode == 'flood':
                    for i in range(300): send('event', dict(index=i), frame)
                elif mode == 'wrong-run': send('result', {}, dict(frame, runId='f' * 32))
                elif mode == 'wrong-sequence':
                    sequence += 1
                    send('result', {}, frame)
                elif mode == 'duplicate-accepted': send('accepted', {}, frame)
                elif mode == 'oversize': stream.write(struct.pack('<I', p.MAX_FRAME + 1))
                elif mode == 'duplicate-json':
                    sequence += 1
                    broken = dict(frame, kind='result', messageId=str(sequence), payload={})
                    raw = json.dumps(broken).replace('"payload": {}', '"payload":{"x":1,"x":2}').encode()
                    stream.write(struct.pack('<I', len(raw)) + raw)
                elif mode == 'logs':
                    sys.stdout.write('X' * 100_000); sys.stdout.flush()
                    sys.stderr.write('Y' * 100_000); sys.stderr.flush()
                    send('result', dict(ok=True), frame)
                elif mode == 'pool':
                    send('result', dict(ok=True, threads={key: os.environ.get(key) for key in (
                        'MABITOOL_COLOR_THREADS', 'OPENBLAS_NUM_THREADS', 'OMP_NUM_THREADS',
                        'MKL_NUM_THREADS', 'NUMEXPR_NUM_THREADS')}), frame)
                elif mode in ('port-gesture', 'port-crash'):
                    send('portRequest', dict(callId='1', method='game.gesture', arguments=dict(
                        gesture=dict(kind='drag', points=[dict(x=10, y=10), dict(x=20, y=20)],
                            right=False, absolute=True, wheelSteps=0,
                            timing=dict(beforeDown=0, afterDown=1, pointInterval=.01, beforeUp=0, afterWheel=0)),
                        remainingSeconds=10)), frame)
                    if mode == 'port-crash': threading.Timer(.4, lambda: os._exit(7)).start()
                elif mode == 'spawn-child':
                    child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'],
                                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    send('result', dict(child=child.pid), frame)
                elif mode == 'port-late':
                    send('portRequest', dict(callId='1', method='game.check', arguments={}), frame)
            elif frame['kind'] in ('portResult', 'portError'):
                if any(frame[key] != active[key] for key in ('requestId', 'runId', 'bindingRevision', 'fencingToken')):
                    os._exit(9)
                send('result', frame['payload'], active)
            elif frame['kind'] == 'command':
                if mode == 'command-ended':
                    send('result', {}, active)
                elif mode == 'command-invalid':
                    send('commandResult', dict(commandId=frame['payload']['commandId'], accepted=True, code=None,
                        interaction=dict(revision='01', batchId=None, selectionEnabled=False, retryEnabled=False)), active)
                elif mode == 'command-wrong-run':
                    send('commandResult', dict(commandId=frame['payload']['commandId'], accepted=False,
                        code='color.runInactive', interaction=None), dict(active, runId='f' * 32))
            elif frame['kind'] == 'cancel' and mode != 'ignore-cancel':
                send('cancelled', {}, active)
            elif frame['kind'] == 'shutdown' and mode != 'ignore-shutdown':
                send('bye')
                return


if __name__ == '__main__': main()
