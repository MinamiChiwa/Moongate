"""Worker v1 严格帧与短期资产引用。"""
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import time

VERSION = 1
MAX_FRAME = 1024 * 1024
FIELDS = {'protocolVersion', 'messageId', 'requestId', 'kind', 'runId',
          'bindingRevision', 'fencingToken', 'payload'}
KINDS = {'hello', 'ready', 'ping', 'pong', 'request', 'accepted', 'event',
         'result', 'error', 'cancel', 'cancelled', 'shutdown', 'bye',
         'portRequest', 'portResult', 'portError', 'command', 'commandResult'}
CONTROL = {'hello', 'ready', 'ping', 'pong', 'shutdown', 'bye'}


class ProtocolError(ValueError): pass


class PipeStream:
    """Windows 重叠管道：允许控制读取与事件写入同时等待。"""

    def __init__(self, name):
        import _winapi
        self.api = _winapi
        self.handle = _winapi.CreateFile('\\\\.\\pipe\\' + name,
            _winapi.GENERIC_READ | _winapi.GENERIC_WRITE, 0, _winapi.NULL,
            _winapi.OPEN_EXISTING, _winapi.FILE_FLAG_OVERLAPPED, _winapi.NULL)

    def _complete(self, operation):
        count, error = operation.GetOverlappedResult(True)
        if error: raise OSError(error, 'Worker pipe operation failed')
        return count

    def read(self, size):
        operation, _ = self.api.ReadFile(self.handle, size, overlapped=True)
        self._complete(operation)
        return operation.getbuffer()

    def write(self, data):
        operation, _ = self.api.WriteFile(self.handle, data, overlapped=True)
        return self._complete(operation)

    def flush(self): pass

    def __enter__(self): return self

    def __exit__(self, *args):
        self.api.CloseHandle(self.handle)


def uint64(value):
    return isinstance(value, str) and re.fullmatch(r'0|[1-9][0-9]{0,19}', value) is not None and int(value) <= 2**64 - 1


def identifier(value):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{32}', value) is not None


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result: raise ProtocolError('worker.frameInvalid')
        result[key] = value
    return result


def invalid_constant(value): raise ProtocolError('worker.frameInvalid')


def validate(frame):
    if (not isinstance(frame, dict) or set(frame) != FIELDS or
            type(frame['protocolVersion']) is not int or frame['protocolVersion'] != VERSION or
            not uint64(frame['messageId']) or frame['messageId'] == '0' or
            not isinstance(frame['kind'], str) or frame['kind'] not in KINDS or
            not isinstance(frame['payload'], dict)):
        raise ProtocolError('worker.frameInvalid')
    scoped = ('requestId', 'runId', 'bindingRevision', 'fencingToken')
    if frame['kind'] in CONTROL:
        if any(frame[k] is not None for k in scoped): raise ProtocolError('worker.frameInvalid')
    elif (not all(identifier(frame[k]) for k in ('requestId', 'runId')) or
          not all(uint64(frame[k]) for k in ('bindingRevision', 'fencingToken'))):
        raise ProtocolError('worker.frameInvalid')
    stack = [(frame, 1)]
    while stack:
        value, depth = stack.pop()
        if depth > 24: raise ProtocolError('worker.frameInvalid')
        if isinstance(value, dict): stack.extend((v, depth + 1) for v in value.values())
        elif isinstance(value, list): stack.extend((v, depth + 1) for v in value)
    return frame


def exact(stream, size):
    chunks = bytearray()
    while len(chunks) < size:
        part = stream.read(size - len(chunks))
        if not part: raise EOFError('worker.disconnected')
        chunks.extend(part)
    return bytes(chunks)


def read(stream):
    length, = struct.unpack('<I', exact(stream, 4))
    if not 0 < length <= MAX_FRAME: raise ProtocolError('worker.frameTooLarge')
    try:
        return validate(json.loads(exact(stream, length).decode('utf-8'),
                        object_pairs_hook=unique, parse_constant=invalid_constant))
    except (UnicodeError, json.JSONDecodeError, RecursionError) as exc:
        raise ProtocolError('worker.frameInvalid') from exc


def encode(frame):
    data = json.dumps(validate(frame), ensure_ascii=False, allow_nan=False, separators=(',', ':')).encode('utf-8')
    if not 0 < len(data) <= MAX_FRAME: raise ProtocolError('worker.frameTooLarge')
    return struct.pack('<I', len(data)) + data


def reject_links(path):
    for current in (path, *path.parents):
        if current.is_symlink() or (hasattr(current, 'is_junction') and current.is_junction()):
            raise ProtocolError('worker.assetInvalid')


def read_asset(root, reference, run_id, *, maximum=MAX_FRAME):
    if (not isinstance(reference, dict) or set(reference) != {'assetId', 'runId', 'sizeBytes', 'sha256', 'expiresAtUnixMs'} or
            reference['runId'] != run_id or not identifier(run_id) or not identifier(reference['assetId']) or
            type(reference['sizeBytes']) is not int or not 0 <= reference['sizeBytes'] <= maximum or
            not isinstance(reference['sha256'], str) or re.fullmatch('[0-9a-f]{64}', reference['sha256']) is None or
            type(reference['expiresAtUnixMs']) is not int or
            not time.time() * 1000 < reference['expiresAtUnixMs'] <= time.time() * 1000 + 300_000):
        raise ProtocolError('worker.assetInvalid')
    root = Path(root)
    if not root.is_absolute(): raise ProtocolError('worker.assetInvalid')
    path = root / run_id / (reference['assetId'] + '.blob')
    reject_links(path)
    if not path.resolve().is_relative_to(root.resolve()): raise ProtocolError('worker.assetInvalid')
    try:
        with path.open('rb') as stream:
            if os.fstat(stream.fileno()).st_size != reference['sizeBytes']: raise ProtocolError('worker.assetInvalid')
            data = stream.read(reference['sizeBytes'] + 1)
        if len(data) != reference['sizeBytes'] or hashlib.sha256(data).hexdigest() != reference['sha256']:
            raise ProtocolError('worker.assetInvalid')
        reject_links(path)
        return data
    except OSError as exc:
        raise ProtocolError('worker.assetInvalid') from exc
