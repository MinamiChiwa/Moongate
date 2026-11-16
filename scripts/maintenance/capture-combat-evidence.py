"""显式采集固定游戏进程的下行 TCP 证据，原始内容仅写入私有临时目录。"""
import argparse
import base64
import csv
import ctypes
from ctypes import wintypes
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import stat
import struct
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
MAXIMUM_BYTES = 32 * 1024 * 1024
MAXIMUM_PACKETS = 100000
MAXIMUM_PACKET = 262144
RECEIVE_BUFFER_BYTES = 16 * 1024 * 1024
MAXIMUM_SCOPE_CHANGES = 4


class CaptureError(RuntimeError):
    pass


def capture_limits(duration, maximum_bytes, maximum_packets, follow_connections=False, remote_port=None,
                   maximum_duration=180):
    if (not all(type(value) is int for value in (duration, maximum_bytes, maximum_packets)) or
            not 1 <= duration <= maximum_duration or not 1 <= maximum_bytes <= MAXIMUM_BYTES or
            not 1 <= maximum_packets <= MAXIMUM_PACKETS or type(follow_connections) is not bool or
            remote_port is not None and (type(remote_port) is not int or not 1 <= remote_port <= 65535)):
        raise CaptureError("capture.budgetInvalid")
    return {"durationSeconds": duration, "maximumBytes": maximum_bytes, "maximumPackets": maximum_packets,
            "snaplenBytes": MAXIMUM_PACKET, "receiveBufferBytes": RECEIVE_BUFFER_BYTES,
            "followConnections": follow_connections,
            "maximumScopeChanges": MAXIMUM_SCOPE_CHANGES if follow_connections else 0,
            "remotePort": remote_port}


@dataclass(frozen=True, order=True)
class Connection:
    local_address: str
    local_port: int
    remote_address: str
    remote_port: int

    def filter(self):
        return (f"(src host {self.remote_address} and src port {self.remote_port} "
                f"and dst host {self.local_address} and dst port {self.local_port})")


@dataclass(frozen=True)
class Device:
    name: bytes
    addresses: frozenset


@dataclass(frozen=True)
class ProcessIdentity:
    pid: int
    creation_ticks: int
    executable: str


def owned_connections(table, pid):
    if len(table) < 4:
        raise CaptureError("capture.connectionsInvalid")
    count = struct.unpack_from("<I", table)[0]
    if 4 + count * 24 > len(table):
        raise CaptureError("capture.connectionsInvalid")
    rows = []
    for index in range(count):
        offset = 4 + index * 24
        state, local, _, remote, _, owner = struct.unpack_from("<6I", table, offset)
        if owner == pid and state == 5:
            rows.append(Connection(str(ipaddress.IPv4Address(struct.pack("<I", local))),
                                   struct.unpack_from("!H", table, offset + 8)[0],
                                   str(ipaddress.IPv4Address(struct.pack("<I", remote))),
                                   struct.unpack_from("!H", table, offset + 16)[0]))
    return rows


class PcapHeader(ctypes.Structure):
    _fields_ = [("seconds", ctypes.c_int32), ("microseconds", ctypes.c_int32),
                ("captured", ctypes.c_uint32), ("length", ctypes.c_uint32)]


def same_process(expected, actual):
    if (expected.pid != actual.pid or expected.creation_ticks != actual.creation_ticks or
            os.path.normcase(expected.executable) != os.path.normcase(actual.executable)):
        raise CaptureError("capture.processChanged")


def parse_packet(link, raw):
    """返回 IPv4 TCP 端点及完整 payload；拒绝截断与 IP 分片。"""
    if len(raw) > MAXIMUM_PACKET:
        return None
    offset = 0
    if link == 1:
        if len(raw) < 14:
            return None
        offset, ether = 14, struct.unpack_from("!H", raw, 12)[0]
        while ether in (0x8100, 0x88A8):
            if len(raw) - offset < 4:
                return None
            ether = struct.unpack_from("!H", raw, offset + 2)[0]
            offset += 4
        if ether != 0x0800:
            return None
    elif link == 0:
        if len(raw) < 4 or struct.unpack_from("<I", raw)[0] != 2:
            return None
        offset = 4
    elif link not in (12, 101, 228):
        return None
    if len(raw) - offset < 20 or raw[offset] >> 4 != 4 or raw[offset + 9] != 6:
        return None
    ihl = (raw[offset] & 15) * 4
    total, fragments = struct.unpack_from("!H2xH", raw, offset + 2)
    if ihl < 20 or total < ihl + 20 or total > len(raw) - offset or fragments & 0x3FFF:
        return None
    if total != len(raw) - offset and not (link == 1 and len(raw) <= 60):
        return None
    tcp = offset + ihl
    tcp_length = (raw[tcp + 12] >> 4) * 4
    if tcp_length < 20 or tcp_length > total - ihl:
        return None
    source_port, destination_port, sequence = struct.unpack_from("!HHI", raw, tcp)
    connection = Connection(str(ipaddress.IPv4Address(raw[offset + 16:offset + 20])), destination_port,
                            str(ipaddress.IPv4Address(raw[offset + 12:offset + 16])), source_port)
    return connection, sequence, raw[tcp + 13], raw[tcp + tcp_length:offset + total]


def is_link(path):
    info = path.lstat()
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)


def output_path(value, root=ROOT):
    path = Path(value)
    if not path.is_absolute() or ".." in path.parts:
        raise CaptureError("capture.outputInvalid")
    root = Path(root).absolute()
    allowed = root / "artifacts" / "tmp"
    if path == allowed or not path.is_relative_to(allowed):
        raise CaptureError("capture.outputInvalid")
    for ancestor in [*reversed(path.parents), path]:
        if (ancestor.exists() or ancestor.is_symlink()) and is_link(ancestor):
            raise CaptureError("capture.outputLinked")
    if path.exists():
        raise CaptureError("capture.outputExists")
    if any(not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}", part)
           or part.endswith(".") for part in path.relative_to(allowed).parts):
        raise CaptureError("capture.outputInvalid")
    return path


def restrict_directory(path):
    result = subprocess.run(["whoami", "/user", "/fo", "csv", "/nh"], capture_output=True,
                            text=True, check=True, creationflags=0x08000000)
    row = next(csv.reader([result.stdout.strip()]))
    sid = row[-1]
    if not re.fullmatch(r"S-1-\d+(?:-\d+)+", sid):
        raise CaptureError("capture.outputAclFailed")
    subprocess.run(["icacls", str(path), "/inheritance:r", "/grant:r", f"*{sid}:(OI)(CI)F"],
                   capture_output=True, check=True, creationflags=0x08000000)


def prepare_output(value, root=ROOT, secure=restrict_directory):
    path = output_path(value, root)
    path.parent.mkdir(parents=True, exist_ok=True)
    output_path(value, root)
    path.mkdir()
    try:
        secure(path)
    except BaseException:
        path.rmdir()
        raise
    return path


class EvidenceWriter:
    def __init__(self, stream, maximum_bytes, maximum_packets):
        if not 1 <= maximum_bytes <= MAXIMUM_BYTES or not 1 <= maximum_packets <= MAXIMUM_PACKETS:
            raise CaptureError("capture.budgetInvalid")
        self.stream = stream
        self.maximum_bytes, self.maximum_packets = maximum_bytes, maximum_packets
        self.bytes = self.packets = self.payload_bytes = 0
        self.digest = hashlib.sha256()

    def write(self, connection_id, link, sequence, flags, payload, monotonic_ms, captured_unix_us=None):
        if self.packets >= self.maximum_packets:
            return "capture.packetBudget"
        record = {"index": self.packets + 1, "monotonicMs": round(monotonic_ms, 3),
                  "connectionId": connection_id, "linktype": link, "tcpSequence": sequence,
                  "tcpFlags": flags, "payloadBase64": base64.b64encode(payload).decode("ascii")}
        if captured_unix_us is not None:
            record["capturedAtUnixMicroseconds"] = str(captured_unix_us)
        encoded = (json.dumps(record, separators=(",", ":")) + "\n").encode("utf-8")
        if self.bytes + len(encoded) > self.maximum_bytes:
            return "capture.byteBudget"
        self.stream.write(encoded)
        self.digest.update(encoded)
        self.bytes += len(encoded)
        self.packets += 1
        self.payload_bytes += len(payload)
        return None


def capture(backend, expected, writer, duration, *, clock=time.monotonic, sleep=time.sleep,
            cancelled=lambda: False, follow_connections=False, remote_port=None):
    if not 1 <= duration <= 180:
        raise CaptureError("capture.durationInvalid")
    capture_limits(duration, writer.maximum_bytes, writer.maximum_packets, follow_connections, remote_port)
    same_process(expected, backend.identity())
    def current_connections():
        return frozenset(row for row in backend.connections() if remote_port is None or row.remote_port == remote_port)
    connections = current_connections()
    if not connections and not follow_connections or len(connections) > 32:
        raise CaptureError("capture.connectionsUnavailable")
    def covers(device, row):
        return (row.local_address in device.addresses or
                device.name.lower().endswith(b"npf_loopback") and
                ipaddress.ip_address(row.local_address).is_loopback and
                ipaddress.ip_address(row.remote_address).is_loopback)
    def scope_devices(rows):
        if not rows:
            return []
        selected = [device for device in backend.devices() if any(covers(device, row) for row in rows)]
        covered = {row for row in rows for device in selected if covers(device, row)}
        if not selected or len(selected) > 8 or covered != rows:
            raise CaptureError("capture.channelUnavailable")
        return selected
    devices = scope_devices(connections)
    identifiers = {}
    handles = []
    started, checked_at = clock(), float("-inf")
    deadline = started + duration
    reason, rejected, dropped, received = "capture.durationComplete", 0, 0, 0
    rejected_reasons = {}
    stats_available = True
    scope_changes = scopes_started = 0
    maximum_connections = len(connections)

    def close_scope():
        nonlocal handles, dropped, stats_available
        previous, handles = handles, []
        for handle, _, _ in reversed(previous):
            try:
                dropped += backend.dropped(handle)
            except (CaptureError, OSError):
                stats_available = False
            finally:
                backend.close(handle)

    def bind_scope(rows, selected=None):
        nonlocal connections, identifiers, scope_changes, scopes_started, maximum_connections
        same_process(expected, backend.identity())
        close_scope()
        connections = rows
        if not rows:
            identifiers = {}
            return
        if len(rows) > 32:
            raise CaptureError("capture.connectionsUnavailable")
        if scopes_started and scope_changes >= MAXIMUM_SCOPE_CHANGES:
            raise CaptureError("capture.scopeBudget")
        chosen = scope_devices(rows) if selected is None else selected
        identifiers = {(device.name, row): uuid.uuid4().hex for device in chosen for row in rows if covers(device, row)}
        for device in chosen:
            scoped = frozenset(row for row in rows if covers(device, row))
            expression = "tcp and (" + " or ".join(row.filter() for row in sorted(scoped)) + ")"
            handles.append((backend.open(device, expression), scoped, device.name))
        if scopes_started:
            scope_changes += 1
        scopes_started += 1
        maximum_connections = max(maximum_connections, len(rows))

    def refresh_scope():
        same_process(expected, backend.identity())
        latest = current_connections()
        if latest == connections:
            return False
        if not follow_connections:
            raise CaptureError("capture.connectionsChanged")
        bind_scope(latest)
        return True

    try:
        bind_scope(connections, devices)
        while clock() - started < duration:
            if cancelled():
                reason = "capture.cancelled"
                break
            now = clock()
            if now - checked_at >= 0.25:
                refresh_scope()
                checked_at = now
            rebound = False
            for handle, scoped, source in handles:
                for _ in range(64):
                    if clock() >= deadline:
                        raise CaptureError("capture.durationComplete")
                    packet = backend.read(handle)
                    if packet is None:
                        break
                    received += 1
                    link, raw, captured_us, invalid = packet
                    if invalid:
                        rejected += 1
                        rejected_reasons[invalid] = rejected_reasons.get(invalid, 0) + 1
                        continue
                    parsed = parse_packet(link, raw)
                    if parsed is None:
                        rejected += 1
                        rejected_reasons["packet.invalidOrFragmented"] = rejected_reasons.get("packet.invalidOrFragmented", 0) + 1
                        continue
                    connection, sequence, flags, payload = parsed
                    if connection not in scoped:
                        rejected += 1
                        rejected_reasons["packet.outOfScope"] = rejected_reasons.get("packet.outOfScope", 0) + 1
                        continue
                    if not payload and not flags & 0x07:
                        continue
                    if refresh_scope():
                        rebound = True
                        break
                    if clock() >= deadline:
                        raise CaptureError("capture.durationComplete")
                    budget = writer.write(identifiers[(source, connection)], link, sequence, flags, payload,
                                          (clock() - started) * 1000, captured_us)
                    if budget:
                        raise CaptureError(budget)
                if rebound:
                    break
            sleep(min(0.01, max(0, deadline - clock())))
        same_process(expected, backend.identity())
    except KeyboardInterrupt:
        reason = "capture.cancelled"
    except CaptureError as error:
        reason = str(error)
    finally:
        close_scope()
    return {"reason": reason, "receivedPackets": received, "rejectedPackets": rejected,
            "droppedPackets": dropped if stats_available else None, "rejectedReasons": rejected_reasons,
            "connectionCount": len(connections), "maximumConcurrentConnections": maximum_connections,
            "followConnections": follow_connections, "scopeChanges": scope_changes, "scopeCount": scopes_started,
            "remotePort": remote_port,
            "maximumScopeChanges": MAXIMUM_SCOPE_CHANGES if follow_connections else 0,
            "connectionScopeContinuity": "discontinuous" if follow_connections else "fixedScope",
            "statisticsStatus": "available" if stats_available else "capture.statsUnavailable",
            "elapsedSeconds": round(clock() - started, 3)}


TRACKED_STATES = frozenset(range(3, 12))
PREARM_RING_BYTES = 16 * 1024 * 1024
PREARM_RING_SECONDS = 5.0
# 跟踪进程连接的采集用于整场战斗取证（Boss 战常超过 10 分钟），时长上限单列；字节与包数预算不变。
TRACKED_MAXIMUM_SECONDS = 900
# 进程连接表会短暂漏列仍在传输的连接：同一四元组在此时间内重新出现时沿用原记录。
TRACKED_RELIST_MS = 5000
TRACK_POLL_SECONDS = 0.1


def owned_rows(table, pid, states=TRACKED_STATES):
    """返回固定进程拥有的连接及其 TCP 状态（含建立中与关闭中的状态）。"""
    if len(table) < 4:
        raise CaptureError("capture.connectionsInvalid")
    count = struct.unpack_from("<I", table)[0]
    if 4 + count * 24 > len(table):
        raise CaptureError("capture.connectionsInvalid")
    rows = {}
    for index in range(count):
        offset = 4 + index * 24
        state, local, _, remote, _, owner = struct.unpack_from("<6I", table, offset)
        if owner == pid and state in states:
            row = Connection(str(ipaddress.IPv4Address(struct.pack("<I", local))),
                             struct.unpack_from("!H", table, offset + 8)[0],
                             str(ipaddress.IPv4Address(struct.pack("<I", remote))),
                             struct.unpack_from("!H", table, offset + 16)[0])
            if row.local_port and row.remote_port:
                rows[row] = state
    return rows


def remote_scope(address):
    value = ipaddress.ip_address(address)
    return "loopback" if value.is_loopback else "private" if value.is_private else "public"


def capture_tracked(backend, expected, writer, duration, *, clock=time.monotonic, sleep=time.sleep,
                    cancelled=lambda: False, ring_bytes=PREARM_RING_BYTES, ring_seconds=PREARM_RING_SECONDS,
                    progress=None):
    """
    跟随固定进程的全部 TCP 连接（不限远端端口），并记录每条连接的元数据。

    覆盖游戏连接本机地址的网卡各开一个只收下行的句柄（dst host 为这些本机地址）。四元组已属于游戏进程的包直接写入；
    尚未出现在连接表中的包只在内存环形缓冲中保留最多 ring_seconds 秒、ring_bytes 字节，连接表随后确认属于游戏进程时
    按原顺序补写，否则丢弃且不落盘。由此保留新连接在被轮询发现之前到达的首批数据。
    """
    if not 1 <= duration <= TRACKED_MAXIMUM_SECONDS:
        raise CaptureError("capture.durationInvalid")
    if not 1 <= ring_bytes <= PREARM_RING_BYTES or not 0 < ring_seconds <= 30:
        raise CaptureError("capture.budgetInvalid")
    same_process(expected, backend.identity())
    started = clock()
    deadline = started + duration
    def elapsed_ms():
        return round((clock() - started) * 1000, 3)
    handles = {}
    addresses = {}
    known = {}
    metadata = []
    ring = []
    ring_size = 0
    reason, rejected, received, dropped = "capture.durationComplete", 0, 0, 0
    rejected_reasons = {}
    stats_available = True
    ring_discarded = ring_flushed = 0
    checked_at = float("-inf")
    reported_at = started

    def report(event):
        # 运行反馈只走 progress 回调（命令行写到标准错误），不影响采集结果与最后一行 JSON。
        if progress:
            progress(event, int(clock() - started), duration, writer.packets,
                     sum(1 for entry in metadata if entry["closedTableMs"] is None))

    def reject(code):
        nonlocal rejected
        rejected += 1
        rejected_reasons[code] = rejected_reasons.get(code, 0) + 1

    def covers(device, row):
        return (row.local_address in device.addresses or
                device.name.lower().endswith(b"npf_loopback") and
                ipaddress.ip_address(row.local_address).is_loopback and
                ipaddress.ip_address(row.remote_address).is_loopback)

    device_list = []
    devices_at = float("-inf")

    def ensure_devices(rows):
        nonlocal device_list, devices_at
        uncovered = [row for row in rows if not any(covers(device, row) for device in device_list)]
        if uncovered and clock() - devices_at >= 1:
            device_list, devices_at = backend.devices(), clock()
        for device in device_list:
            wanted = sorted({row.local_address for row in rows if covers(device, row)})
            if not wanted:
                continue
            current = addresses.get(device.name)
            if current is not None and set(wanted) <= set(current):
                continue
            merged = sorted(set(wanted) | set(current or []))
            if current is None and len(handles) >= 8:
                raise CaptureError("capture.channelUnavailable")
            expression = "tcp and (" + " or ".join(f"dst host {address}" for address in merged) + ")"
            opened = backend.open(device, expression)
            previous = handles.get(device.name)
            handles[device.name] = opened
            addresses[device.name] = merged
            if previous is not None:
                close_handle(previous)

    def close_handle(handle):
        nonlocal dropped, stats_available
        try:
            dropped += backend.dropped(handle)
        except (CaptureError, OSError):
            stats_available = False
        finally:
            backend.close(handle)

    def write(entry, row, at_ms, link, sequence, flags, payload, captured_us, prearmed):
        if not payload and not flags & 0x07:
            return None
        if flags & 0x12 == 0x12 and entry["synAckMs"] is None:
            entry["synAckMs"] = at_ms
        if flags & 0x01 and entry["finMs"] is None:
            entry["finMs"] = at_ms
        if flags & 0x04 and entry["rstMs"] is None:
            entry["rstMs"] = at_ms
        entry["firstPacketMs"] = at_ms if entry["firstPacketMs"] is None else entry["firstPacketMs"]
        entry["lastPacketMs"] = at_ms
        entry["downlinkPackets"] += 1
        entry["downlinkPayloadBytes"] += len(payload)
        if prearmed:
            entry["prearmedPackets"] += 1
        return writer.write(entry["connectionId"], link, sequence, flags, payload, at_ms, captured_us)

    def refresh():
        nonlocal ring, ring_size, ring_flushed, ring_discarded
        same_process(expected, backend.identity())
        rows = backend.tracked_connections()
        if len(rows) > 64:
            raise CaptureError("capture.connectionsUnavailable")
        now = elapsed_ms()
        for row, state in rows.items():
            entry = known.get(row)
            if entry is not None and entry["closedTableMs"] is not None:
                if now - entry["closedTableMs"] <= TRACKED_RELIST_MS:
                    entry["closedTableMs"] = None
                else:
                    entry = None
            if entry is None:
                if len(metadata) >= 256:
                    raise CaptureError("capture.connectionsUnavailable")
                entry = {"connectionId": uuid.uuid4().hex, "localPort": row.local_port, "remotePort": row.remote_port,
                         "remoteScope": remote_scope(row.remote_address), "firstSeenTableMs": now, "lastSeenTableMs": now,
                         "closedTableMs": None, "states": [], "covered": False, "synAckMs": None, "finMs": None, "rstMs": None,
                         "firstPacketMs": None, "lastPacketMs": None, "downlinkPackets": 0, "downlinkPayloadBytes": 0,
                         "prearmedPackets": 0}
                known[row] = entry
                metadata.append(entry)
                report(f"发现连接 本地端口 {row.local_port} → 远端端口 {row.remote_port}")
            entry["lastSeenTableMs"] = now
            name = {3: "synSent", 4: "synReceived", 5: "established", 6: "finWait1", 7: "finWait2", 8: "closeWait",
                    9: "closing", 10: "lastAck", 11: "timeWait"}.get(state, str(state))
            if not entry["states"] or entry["states"][-1]["state"] != name:
                if len(entry["states"]) < 16:
                    entry["states"].append({"state": name, "atMs": now})
        for row, entry in known.items():
            if row not in rows and entry["closedTableMs"] is None:
                entry["closedTableMs"] = now
                report(f"连接表不再列出 本地端口 {row.local_port} → 远端端口 {row.remote_port}")
        ensure_devices(set(rows))
        active = [device for device in device_list if device.name in handles]
        for row, entry in known.items():
            entry["covered"] = entry["covered"] or any(covers(device, row) for device in active)
        while ring and clock() - ring[0][0] > ring_seconds:
            ring_size -= len(ring.pop(0)[6])
            ring_discarded += 1
        kept = []
        for item in ring:
            row = item[1]
            entry = known.get(row)
            if entry is not None and entry["closedTableMs"] is None:
                budget = write(entry, row, *item[2:], True)
                if budget:
                    raise CaptureError(budget)
                ring_flushed += 1
                ring_size -= len(item[6])
            else:
                kept.append(item)
        ring = kept

    try:
        refresh()
        while clock() < deadline:
            if cancelled():
                reason = "capture.cancelled"
                break
            now = clock()
            if now - checked_at >= TRACK_POLL_SECONDS:
                refresh()
                checked_at = now
            if now - reported_at >= 5:
                report(None)
                reported_at = now
            for handle in list(handles.values()):
                for _ in range(256):
                    if clock() >= deadline:
                        raise CaptureError("capture.durationComplete")
                    packet = backend.read(handle)
                    if packet is None:
                        break
                    received += 1
                    link, raw, captured_us, invalid = packet
                    if invalid:
                        reject(invalid)
                        continue
                    parsed = parse_packet(link, raw)
                    if parsed is None:
                        reject("packet.invalidOrFragmented")
                        continue
                    row, sequence, flags, payload = parsed
                    at_ms = elapsed_ms()
                    entry = known.get(row)
                    if entry is not None:
                        budget = write(entry, row, at_ms, link, sequence, flags, payload, captured_us, False)
                        if budget:
                            raise CaptureError(budget)
                        continue
                    ring.append((clock(), row, at_ms, link, sequence, flags, payload, captured_us))
                    ring_size += len(payload)
                    while ring and (ring_size > ring_bytes or clock() - ring[0][0] > ring_seconds):
                        ring_size -= len(ring.pop(0)[6])
                        ring_discarded += 1
            sleep(min(0.005, max(0, deadline - clock())))
        same_process(expected, backend.identity())
    except KeyboardInterrupt:
        reason = "capture.cancelled"
    except CaptureError as error:
        reason = str(error)
    finally:
        for handle in list(handles.values()):
            close_handle(handle)
        handles.clear()
        ring_discarded += len(ring)
        ring.clear()
    return {"reason": reason, "receivedPackets": received, "rejectedPackets": rejected,
            "droppedPackets": dropped if stats_available else None, "rejectedReasons": rejected_reasons,
            "trackProcessConnections": True, "connectionCount": len(metadata), "deviceCount": len(addresses),
            "prearmFlushedPackets": ring_flushed, "prearmDiscardedPackets": ring_discarded,
            "prearmRingBytes": ring_bytes, "prearmRingSeconds": ring_seconds,
            "statisticsStatus": "available" if stats_available else "capture.statsUnavailable",
            "elapsedSeconds": round(clock() - started, 3), "connections": metadata}


class NativeBackend:
    def __init__(self, pid, executable):
        if os.name != "nt" or not Path(executable).is_absolute():
            raise CaptureError("capture.platformOrPathInvalid")
        self.pid, self.executable = pid, str(Path(executable).resolve(strict=True))
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.ip = ctypes.WinDLL("iphlpapi", use_last_error=True)
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        self.kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        self.kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                                       wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        self.ip.GetExtendedTcpTable.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD),
                                              wintypes.BOOL, wintypes.ULONG, ctypes.c_int, wintypes.ULONG]
        self.handle = self.kernel.OpenProcess(0x1000 | 0x100000, False, pid)
        if not self.handle:
            raise CaptureError("capture.processUnavailable")
        try:
            self.initial = self.identity()
            system = ctypes.create_unicode_buffer(32768)
            self.kernel.GetSystemDirectoryW.argtypes = [wintypes.LPWSTR, wintypes.UINT]
            if not self.kernel.GetSystemDirectoryW(system, len(system)):
                raise CaptureError("capture.systemDirectoryUnavailable")
            dll = Path(system.value) / "Npcap" / "wpcap.dll"
            self.pcap = ctypes.CDLL(str(dll), winmode=0x100 | 0x800)
            self._configure_pcap()
        except BaseException:
            self.dispose()
            raise

    def identity(self):
        code = wintypes.DWORD()
        created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
        buffer, size = ctypes.create_unicode_buffer(32768), wintypes.DWORD(32768)
        if (not self.kernel.GetExitCodeProcess(self.handle, ctypes.byref(code)) or code.value != 259 or
                not self.kernel.GetProcessTimes(self.handle, ctypes.byref(created), ctypes.byref(exited),
                                                ctypes.byref(kernel), ctypes.byref(user)) or
                not self.kernel.QueryFullProcessImageNameW(self.handle, 0, buffer, ctypes.byref(size))):
            raise CaptureError("capture.processChanged")
        actual = str(Path(buffer.value).resolve(strict=True))
        if os.path.normcase(actual) != os.path.normcase(self.executable):
            raise CaptureError("capture.processChanged")
        return ProcessIdentity(self.pid, created.dwLowDateTime | created.dwHighDateTime << 32, actual)

    def connections(self):
        return owned_connections(self._table(), self.pid)

    def tracked_connections(self):
        return owned_rows(self._table(), self.pid)

    def _table(self):
        size = wintypes.DWORD()
        status = self.ip.GetExtendedTcpTable(None, ctypes.byref(size), False, 2, 5, 0)
        if status not in (0, 122) or size.value > 16 * 1024 * 1024:
            raise CaptureError("capture.connectionsUnavailable")
        for _ in range(3):
            buffer = ctypes.create_string_buffer(max(4, size.value))
            status = self.ip.GetExtendedTcpTable(buffer, ctypes.byref(size), False, 2, 5, 0)
            if status == 122 and size.value <= 16 * 1024 * 1024:
                continue
            if status:
                raise CaptureError("capture.connectionsUnavailable")
            return buffer.raw
        raise CaptureError("capture.connectionsUnavailable")

    def _configure_pcap(self):
        class PcapAddress(ctypes.Structure):
            pass
        class PcapDevice(ctypes.Structure):
            pass
        PcapAddress._fields_ = [("next", ctypes.POINTER(PcapAddress)), ("address", ctypes.c_void_p),
                               ("netmask", ctypes.c_void_p), ("broadcast", ctypes.c_void_p), ("destination", ctypes.c_void_p)]
        PcapDevice._fields_ = [("next", ctypes.POINTER(PcapDevice)), ("name", ctypes.c_char_p),
                              ("description", ctypes.c_char_p), ("addresses", ctypes.POINTER(PcapAddress)), ("flags", ctypes.c_uint)]
        class Bpf(ctypes.Structure):
            _fields_ = [("length", ctypes.c_uint), ("instructions", ctypes.c_void_p)]
        class Stats(ctypes.Structure):
            _fields_ = [("received", ctypes.c_uint), ("dropped", ctypes.c_uint), ("interface_dropped", ctypes.c_uint),
                        ("reserved", ctypes.c_uint * 13)]
        self.Device, self.Bpf, self.Header, self.Stats = PcapDevice, Bpf, PcapHeader, Stats
        signatures = {
            "pcap_findalldevs": ([ctypes.POINTER(ctypes.POINTER(PcapDevice)), ctypes.c_char_p], ctypes.c_int),
            "pcap_freealldevs": ([ctypes.POINTER(PcapDevice)], None),
            "pcap_create": ([ctypes.c_char_p, ctypes.c_char_p], ctypes.c_void_p),
            "pcap_set_snaplen": ([ctypes.c_void_p, ctypes.c_int], ctypes.c_int),
            "pcap_set_buffer_size": ([ctypes.c_void_p, ctypes.c_int], ctypes.c_int),
            "pcap_set_promisc": ([ctypes.c_void_p, ctypes.c_int], ctypes.c_int),
            "pcap_set_timeout": ([ctypes.c_void_p, ctypes.c_int], ctypes.c_int),
            "pcap_activate": ([ctypes.c_void_p], ctypes.c_int),
            "pcap_compile": ([ctypes.c_void_p, ctypes.POINTER(Bpf), ctypes.c_char_p, ctypes.c_int, ctypes.c_uint32], ctypes.c_int),
            "pcap_setfilter": ([ctypes.c_void_p, ctypes.POINTER(Bpf)], ctypes.c_int),
            "pcap_freecode": ([ctypes.POINTER(Bpf)], None),
            "pcap_setnonblock": ([ctypes.c_void_p, ctypes.c_int, ctypes.c_char_p], ctypes.c_int),
            "pcap_datalink": ([ctypes.c_void_p], ctypes.c_int),
            "pcap_next_ex": ([ctypes.c_void_p, ctypes.POINTER(ctypes.POINTER(PcapHeader)), ctypes.POINTER(ctypes.c_void_p)], ctypes.c_int),
            "pcap_stats": ([ctypes.c_void_p, ctypes.POINTER(Stats)], ctypes.c_int),
            "pcap_close": ([ctypes.c_void_p], None),
        }
        for name, (arguments, result) in signatures.items():
            function = getattr(self.pcap, name)
            function.argtypes, function.restype = arguments, result

    def devices(self):
        head, error = ctypes.POINTER(self.Device)(), ctypes.create_string_buffer(256)
        if self.pcap.pcap_findalldevs(ctypes.byref(head), error):
            raise CaptureError("capture.channelsUnavailable")
        result = []
        try:
            current = head
            while current:
                row, addresses = current.contents, set()
                address = row.addresses
                while address:
                    pointer = address.contents.address
                    if pointer and ctypes.c_ushort.from_address(pointer).value == 2:
                        addresses.add(str(ipaddress.IPv4Address(ctypes.string_at(pointer + 4, 4))))
                    address = address.contents.next
                result.append(Device(row.name, frozenset(addresses)))
                current = row.next
        finally:
            self.pcap.pcap_freealldevs(head)
        return result

    def open(self, device, expression):
        error = ctypes.create_string_buffer(256)
        handle = self.pcap.pcap_create(device.name, error)
        if not handle:
            raise CaptureError("capture.openFailed")
        try:
            for method, value in [("pcap_set_snaplen", MAXIMUM_PACKET), ("pcap_set_buffer_size", RECEIVE_BUFFER_BYTES),
                                  ("pcap_set_promisc", 0), ("pcap_set_timeout", 100)]:
                if getattr(self.pcap, method)(handle, value):
                    raise CaptureError("capture.configureFailed")
            if self.pcap.pcap_activate(handle) < 0:
                raise CaptureError("capture.activateFailed")
            program = self.Bpf()
            if self.pcap.pcap_compile(handle, ctypes.byref(program), expression.encode("ascii"), 1, 0xFFFFFFFF):
                raise CaptureError("capture.filterFailed")
            try:
                if self.pcap.pcap_setfilter(handle, ctypes.byref(program)):
                    raise CaptureError("capture.filterFailed")
            finally:
                self.pcap.pcap_freecode(ctypes.byref(program))
            if self.pcap.pcap_setnonblock(handle, 1, error):
                raise CaptureError("capture.nonblockingFailed")
            link = self.pcap.pcap_datalink(handle)
            if link not in (0, 1, 12, 101, 228):
                raise CaptureError("capture.linkUnsupported")
            return handle, link
        except BaseException:
            self.pcap.pcap_close(handle)
            raise

    def read(self, handle):
        native, link = handle
        header, data = ctypes.POINTER(self.Header)(), ctypes.c_void_p()
        status = self.pcap.pcap_next_ex(native, ctypes.byref(header), ctypes.byref(data))
        if status == 0:
            return None
        if status != 1 or not header or not data:
            raise CaptureError("capture.readFailed")
        size, length = header.contents.captured, header.contents.length
        if size == 0 or size > MAXIMUM_PACKET or size != length:
            return link, b"", None, "packet.truncatedOrOversized"
        stamp = header.contents.seconds * 1000000 + header.contents.microseconds
        return link, ctypes.string_at(data, size), stamp, None

    def dropped(self, handle):
        value = self.Stats()
        if self.pcap.pcap_stats(handle[0], ctypes.byref(value)):
            raise CaptureError("capture.statsUnavailable")
        return value.dropped + value.interface_dropped

    def close(self, handle):
        self.pcap.pcap_close(handle[0])

    def dispose(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


def client_files(executable):
    root = Path(executable).parent
    files = [root / "GameAssembly.dll", root / "UnityPlayer.dll",
             root / "MabinogiMobile_Data/il2cpp_data/Metadata/global-metadata.dat"]
    result = []
    for path in files:
        with path.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        result.append({"file": path.relative_to(root).as_posix(), "bytes": path.stat().st_size, "sha256": digest})
    return result


def show_progress(event, elapsed, duration, packets, connections):
    """采集期间的运行反馈：计时、已写入包数与当前连接数，连接出现或结束时另起一行。"""
    text = f"[{elapsed:>3}/{duration} 秒] 已写入 {packets} 包，当前连接 {connections} 条"
    print(text + (f"｜{event}" if event else ""), file=sys.stderr, flush=True)


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--game-exe", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--duration-seconds", type=int, default=60)
    parser.add_argument("--maximum-bytes", type=int, default=MAXIMUM_BYTES)
    parser.add_argument("--maximum-packets", type=int, default=MAXIMUM_PACKETS)
    parser.add_argument("--follow-connections", action="store_true",
                        help="在同一进程身份及总预算内最多重新绑定四次；各连接范围之间不连续")
    parser.add_argument("--remote-port", type=int,
                        help="仅采集固定进程拥有且远端端口匹配的连接；默认保留全部拥有连接")
    parser.add_argument("--track-process-connections", action="store_true",
                        help="跟随固定进程的全部 TCP 连接（不限端口），预缓冲新连接被发现前的下行包，并写 connections.json 元数据")
    args = parser.parse_args(arguments)
    if args.pid <= 0:
        parser.error("采集参数超过预算")
    if args.track_process_connections and (args.follow_connections or args.remote_port is not None):
        parser.error("--track-process-connections 不能与 --follow-connections/--remote-port 同时使用")
    try:
        limits = capture_limits(args.duration_seconds, args.maximum_bytes, args.maximum_packets,
                                args.follow_connections, args.remote_port,
                                TRACKED_MAXIMUM_SECONDS if args.track_process_connections else 180)
    except CaptureError:
        parser.error("采集参数超过预算")
    if args.track_process_connections:
        limits.update({"trackProcessConnections": True, "prearmRingBytes": PREARM_RING_BYTES,
                       "prearmRingSeconds": PREARM_RING_SECONDS})
    backend, output = None, None
    report = {"schemaVersion": 1, "sampleKind": "local-private-capture", "combatFieldsVerified": False,
              "startedAt": datetime.now(timezone.utc).isoformat(), "reason": "capture.setupFailed", "limits": limits}
    try:
        backend = NativeBackend(args.pid, args.game_exe)
        report["process"] = {"pid": args.pid, "creationTicks": str(backend.initial.creation_ticks)}
        report["clientFiles"] = client_files(args.game_exe)
        same_process(backend.initial, backend.identity())
        output = prepare_output(args.output_dir)
        print(f"采集开始：游戏进程 {args.pid}，时长 {args.duration_seconds} 秒，输出 {output}。按 Ctrl+C 可提前结束。",
              file=sys.stderr, flush=True)
        with (output / "packets.jsonl").open("xb") as stream:
            writer = EvidenceWriter(stream, args.maximum_bytes, args.maximum_packets)
            try:
                if args.track_process_connections:
                    tracked = capture_tracked(backend, backend.initial, writer, args.duration_seconds,
                                              progress=show_progress)
                    with (output / "connections.json").open("x", encoding="utf-8", newline="\n") as rows:
                        rows.write(json.dumps({"schemaVersion": 1, "connections": tracked.pop("connections")},
                                              ensure_ascii=False, indent=2) + "\n")
                    report.update(tracked)
                else:
                    report.update(capture(backend, backend.initial, writer, args.duration_seconds,
                                          follow_connections=args.follow_connections, remote_port=args.remote_port))
            finally:
                stream.flush()
                os.fsync(stream.fileno())
                report.update({"writtenPackets": writer.packets, "writtenBytes": writer.bytes,
                               "payloadBytes": writer.payload_bytes, "rawSha256": writer.digest.hexdigest()})
        after = client_files(args.game_exe)
        if after != report["clientFiles"]:
            report["reason"] = "capture.clientFilesChanged"
    except CaptureError as error:
        report["reason"] = str(error)
    except KeyboardInterrupt:
        report["reason"] = "capture.cancelled"
    except (OSError, subprocess.SubprocessError, ValueError):
        report["reason"] = "capture.ioFailed"
    finally:
        if backend:
            backend.dispose()
        report["finishedAt"] = datetime.now(timezone.utc).isoformat()
        if output:
            with (output / "summary.json").open("x", encoding="utf-8", newline="\n") as summary:
                summary.write(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(f"采集结束：{report['reason']}，写入 {report.get('writtenPackets', 0)} 包。", file=sys.stderr, flush=True)
    print(json.dumps({"reason": report["reason"], "writtenPackets": report.get("writtenPackets", 0),
                      "writtenBytes": report.get("writtenBytes", 0), "outputDirectory": str(output) if output else None}, ensure_ascii=False))
    return 0 if report["reason"] in {"capture.durationComplete", "capture.byteBudget", "capture.packetBudget", "capture.cancelled"} else 1


if __name__ == "__main__":
    raise SystemExit(main())
