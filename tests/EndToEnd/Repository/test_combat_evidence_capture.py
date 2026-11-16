"""被动证据采集的作用域、预算和原生资源生命周期回归。"""
import base64
import ctypes
import importlib.util
import io
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("combat_evidence_capture", ROOT / "scripts/maintenance/capture-combat-evidence.py")
capture = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = capture
SPEC.loader.exec_module(capture)


def packet(payload=b"payload", *, inbound=True, fragment=0, link=1):
    local, remote = bytes([192, 0, 2, 1]), bytes([198, 51, 100, 1])
    source, destination = (remote, local) if inbound else (local, remote)
    source_port, destination_port = (1111, 50000) if inbound else (50000, 1111)
    ip = bytearray(20)
    struct.pack_into("!BBHHHBBH4s4s", ip, 0, 0x45, 0, 40 + len(payload), 0, fragment, 64, 6, 0, source, destination)
    tcp = bytearray(20)
    struct.pack_into("!HHI", tcp, 0, source_port, destination_port, 0xFFFFFFFF)
    tcp[12:14] = b"\x50\x18"
    prefix = b"\0" * 12 + b"\x08\0" if link == 1 else struct.pack("<I", 2) if link == 0 else b""
    return prefix + ip + tcp + payload


class Clock:
    def __init__(self):
        self.now = 0
    def __call__(self):
        return self.now
    def sleep(self, duration):
        self.now += duration


class FakeBackend:
    identity_value = capture.ProcessIdentity(11, 100, "game.exe")
    connection = capture.Connection("192.0.2.1", 50000, "198.51.100.1", 1111)
    def __init__(self, packets=()):
        self.packets = list(packets)
        self.opened, self.closed, self.filters = [], [], []
        self.identities = 0
        self.identity_change = None
        self.connection_checks = 0
        self.connection_change = None
        self.failure = None
    def identity(self):
        self.identities += 1
        if self.identity_change and self.identities >= self.identity_change:
            return capture.ProcessIdentity(11, 101, "game.exe")
        return self.identity_value
    def connections(self):
        self.connection_checks += 1
        return [] if self.connection_change and self.connection_checks >= self.connection_change else [self.connection]
    def devices(self):
        return [capture.Device(b"fixture-device", frozenset(["192.0.2.1"])),
                capture.Device(b"unrelated-device", frozenset(["203.0.113.1"]))]
    def open(self, device, expression):
        self.filters.append(expression)
        self.opened.append(device.name)
        return device.name
    def read(self, handle):
        if self.failure:
            raise self.failure
        return self.packets.pop(0) if self.packets else None
    def dropped(self, handle):
        return 2
    def close(self, handle):
        self.closed.append(handle)


class FollowingBackend(FakeBackend):
    def __init__(self, clock, *, always_empty=False, rapid_changes=False):
        super().__init__()
        self.clock = clock
        self.always_empty, self.rapid_changes = always_empty, rapid_changes
        self.by_handle, self.events = {}, []

    def connections(self):
        if self.always_empty or not self.rapid_changes and 0.2 <= self.clock.now < 0.6:
            return []
        generation = int((self.clock.now + 0.000001) / 0.2) if self.rapid_changes else int(self.clock.now >= 0.6)
        return [capture.Connection("192.0.2.1", 50000 + generation, "198.51.100.1", 1111)]

    def open(self, device, expression):
        handle = len(self.opened) + 1
        self.opened.append(handle)
        self.events.append(("open", handle))
        self.filters.append(expression)
        port = self.connections()[0].local_port
        raw = bytearray(packet())
        struct.pack_into("!H", raw, 36, port)
        struct.pack_into("!I", raw, 38, handle * 10)
        self.by_handle[handle] = [(1, bytes(raw), 100 + handle, None)]
        if handle > 1:
            self.by_handle[handle].insert(0, (1, packet(), 100, None))
        return handle

    def read(self, handle):
        rows = self.by_handle[handle]
        return rows.pop(0) if rows else None

    def close(self, handle):
        super().close(handle)
        self.events.append(("close", handle))


class CombatEvidenceCaptureTests(unittest.TestCase):
    def run_capture(self, backend, *, maximum_bytes=capture.MAXIMUM_BYTES, maximum_packets=100,
                    cancelled=lambda: False, follow_connections=False, remote_port=None):
        stream, clock = io.BytesIO(), Clock()
        writer = capture.EvidenceWriter(stream, maximum_bytes, maximum_packets)
        result = capture.capture(backend, backend.identity_value, writer, 1, clock=clock,
                                 sleep=clock.sleep, cancelled=cancelled, follow_connections=follow_connections,
                                 remote_port=remote_port)
        rows = [json.loads(line) for line in stream.getvalue().splitlines()]
        return result, rows, writer

    def test_only_matching_inbound_tcp_is_written_and_addresses_are_not_serialized(self):
        backend = FakeBackend([(1, packet(inbound=False), 1000000, None), (1, packet(), 1000001, None)])
        result, rows, writer = self.run_capture(backend)
        self.assertEqual(1, writer.packets)
        self.assertEqual("capture.durationComplete", result["reason"])
        self.assertEqual(1, result["rejectedReasons"]["packet.outOfScope"])
        self.assertEqual("1000001", rows[0]["capturedAtUnixMicroseconds"])
        self.assertEqual(0xFFFFFFFF, rows[0]["tcpSequence"])
        self.assertNotIn("192.0.2.1", json.dumps(rows))
        self.assertNotIn("198.51.100.1", json.dumps(rows))
        self.assertEqual(backend.opened, backend.closed)
        self.assertEqual([b"fixture-device"], backend.opened)
        self.assertIn("src port 1111", backend.filters[0])
        self.assertNotIn("src port 50000", backend.filters[0])

    def test_retransmission_payloads_are_preserved_for_later_stream_reassembly(self):
        raw = packet()
        _, rows, _ = self.run_capture(FakeBackend([(1, raw, 100, None), (1, raw, 101, None)]))
        self.assertEqual(2, len(rows))
        self.assertEqual(rows[0]["tcpSequence"], rows[1]["tcpSequence"])
        self.assertEqual(rows[0]["payloadBase64"], rows[1]["payloadBase64"])
        self.assertEqual(rows[0]["connectionId"], rows[1]["connectionId"])

    def test_same_endpoint_on_two_capture_devices_keeps_independent_stream_ids(self):
        backend = FakeBackend()
        backend.devices = lambda: [capture.Device(b"physical", frozenset(["192.0.2.1"])),
                                   capture.Device(b"virtual", frozenset(["192.0.2.1"]))]
        remaining = {b"physical": 2, b"virtual": 2}
        def read(handle):
            if not remaining[handle]:
                return None
            remaining[handle] -= 1
            raw = bytearray(packet())
            struct.pack_into("!I", raw, 38, 10 if handle == b"physical" else 5000)
            return 1, bytes(raw), 100, None
        backend.read = read
        result, rows, _ = self.run_capture(backend)
        self.assertEqual(4, len(rows))
        physical, virtual = rows[:2], rows[2:]
        self.assertEqual(physical[0]["connectionId"], physical[1]["connectionId"])
        self.assertEqual(virtual[0]["connectionId"], virtual[1]["connectionId"])
        self.assertNotEqual(physical[0]["connectionId"], virtual[0]["connectionId"])
        self.assertEqual([10, 10, 5000, 5000], [row["tcpSequence"] for row in rows])
        self.assertNotIn("physical", json.dumps(rows))
        self.assertNotIn("virtual", json.dumps(rows))
        self.assertEqual(4, result["droppedPackets"])

    def test_link_types_and_vlan_frames_preserve_the_same_tcp_payload(self):
        for link in (0, 1, 12, 101, 228):
            with self.subTest(link=link):
                parsed = capture.parse_packet(link, packet(link=link))
                self.assertEqual(b"payload", parsed[3])
        raw = packet()
        vlan = raw[:12] + b"\x81\x00\x00\x01\x88\xa8\x00\x02\x08\x00" + raw[14:]
        self.assertEqual(b"payload", capture.parse_packet(1, vlan)[3])

    def test_truncated_fragmented_non_tcp_and_invalid_tcp_headers_are_rejected(self):
        for raw in (packet()[:-1], packet() + b"extra-payload", packet(fragment=1), packet(fragment=0x2000), b"", b"x" * 10):
            self.assertIsNone(capture.parse_packet(1, raw))
        non_tcp = bytearray(packet()); non_tcp[23] = 17
        self.assertIsNone(capture.parse_packet(1, non_tcp))
        bad_tcp = bytearray(packet()); bad_tcp[46] = 0x10
        self.assertIsNone(capture.parse_packet(1, bad_tcp))
        self.assertIsNone(capture.parse_packet(229, packet()))

    def test_packet_lengths_and_every_rejection_are_counted(self):
        backend = FakeBackend([(1, b"", None, "packet.truncatedOrOversized"),
                               (1, packet(fragment=1), None, None)])
        result, rows, _ = self.run_capture(backend)
        self.assertEqual([], rows)
        self.assertEqual(2, result["rejectedPackets"])
        self.assertEqual({"packet.truncatedOrOversized": 1, "packet.invalidOrFragmented": 1}, result["rejectedReasons"])

    def test_byte_budget_covers_encoded_json_and_never_writes_a_partial_line(self):
        stream = io.BytesIO()
        writer = capture.EvidenceWriter(stream, 100, 5)
        self.assertEqual("capture.byteBudget", writer.write("a" * 32, 1, 1, 16, b"a" * 50, 0))
        self.assertEqual(b"", stream.getvalue())
        self.assertEqual(0, writer.bytes)
        result, rows, writer = self.run_capture(FakeBackend([(1, packet(), None, None)] * 2), maximum_packets=1)
        self.assertEqual("capture.packetBudget", result["reason"])
        self.assertEqual(1, len(rows))
        self.assertEqual(writer.bytes, len(writer.stream.getvalue()))

    def test_report_limits_preserve_requested_and_native_receive_budgets(self):
        self.assertEqual({"durationSeconds": 12, "maximumBytes": 2048, "maximumPackets": 7,
                          "snaplenBytes": 262144, "receiveBufferBytes": 16777216,
                          "followConnections": False, "maximumScopeChanges": 0, "remotePort": None},
                         capture.capture_limits(12, 2048, 7))
        followed = capture.capture_limits(12, 2048, 7, True)
        self.assertTrue(followed["followConnections"])
        self.assertEqual(4, followed["maximumScopeChanges"])
        self.assertEqual(12345, capture.capture_limits(12, 2048, 7, True, 12345)["remotePort"])
        for arguments in [(0, 1, 1), (181, 1, 1), (1, capture.MAXIMUM_BYTES + 1, 1),
                          (1, 1, capture.MAXIMUM_PACKETS + 1), (True, 1, 1)]:
            with self.assertRaisesRegex(capture.CaptureError, "budgetInvalid"):
                capture.capture_limits(*arguments)

    def test_explicit_remote_port_rejects_invalid_values_before_capture(self):
        for port in (0, 65536, -1, True, "1111"):
            with self.subTest(port=port), self.assertRaisesRegex(capture.CaptureError, "budgetInvalid"):
                capture.capture_limits(1, 1024, 10, False, port)

    def test_duration_uses_monotonic_time_including_packet_read_and_scope_checks(self):
        backend, clock, stream = FakeBackend(), Clock(), io.BytesIO()
        count = 0
        def slow_read(_):
            nonlocal count
            count += 1
            clock.now += 0.6
            return 1, packet(), 100, None
        backend.read = slow_read
        writer = capture.EvidenceWriter(stream, capture.MAXIMUM_BYTES, 10)
        result = capture.capture(backend, backend.identity_value, writer, 1, clock=clock, sleep=clock.sleep)
        self.assertEqual("capture.durationComplete", result["reason"])
        self.assertEqual(2, count)
        self.assertEqual(1, writer.packets)
        self.assertEqual(600, json.loads(stream.getvalue())["monotonicMs"])
        self.assertEqual(1.2, result["elapsedSeconds"])
        self.assertEqual(backend.opened, backend.closed)

    def test_cancellation_read_error_and_identity_change_release_capture_handles(self):
        backend = FakeBackend()
        result, _, _ = self.run_capture(backend, cancelled=lambda: True)
        self.assertEqual("capture.cancelled", result["reason"])
        self.assertEqual(2, result["droppedPackets"])
        self.assertEqual(backend.opened, backend.closed)
        backend = FakeBackend(); backend.failure = capture.CaptureError("capture.readFailed")
        result, _, _ = self.run_capture(backend)
        self.assertEqual("capture.readFailed", result["reason"])
        self.assertEqual(2, result["droppedPackets"])
        self.assertEqual(backend.opened, backend.closed)
        backend = FakeBackend(); backend.identity_change = 2
        result, _, _ = self.run_capture(backend)
        self.assertEqual("capture.processChanged", result["reason"])
        self.assertEqual(backend.opened, backend.closed)

    def test_unavailable_statistics_are_explicit_for_cancellation_failure_and_budget_exit(self):
        def unavailable(_):
            raise capture.CaptureError("capture.statsUnavailable")
        cases = [(False, False, "capture.cancelled"), (True, False, "capture.readFailed"),
                 (False, True, "capture.packetBudget")]
        for fail_read, budget, reason in cases:
            with self.subTest(reason=reason):
                backend = FakeBackend([(1, packet(), 100, None)] * 2)
                backend.dropped = unavailable
                if fail_read:
                    backend.failure = capture.CaptureError("capture.readFailed")
                result, _, _ = self.run_capture(backend, maximum_packets=1,
                                                cancelled=lambda: not fail_read and not budget)
                self.assertEqual(reason, result["reason"])
                self.assertIsNone(result["droppedPackets"])
                self.assertEqual("capture.statsUnavailable", result["statisticsStatus"])
                self.assertEqual(backend.opened, backend.closed)

    def test_changed_connections_stop_without_expanding_the_filter(self):
        backend = FakeBackend(); backend.connection_change = 2
        result, _, _ = self.run_capture(backend)
        self.assertEqual("capture.connectionsChanged", result["reason"])
        self.assertEqual(1, len(backend.filters))
        self.assertEqual(backend.opened, backend.closed)

    def test_follow_mode_rebinds_only_current_process_connections_and_keeps_scope_ids_distinct(self):
        clock, stream = Clock(), io.BytesIO()
        backend = FollowingBackend(clock)
        writer = capture.EvidenceWriter(stream, capture.MAXIMUM_BYTES, 100)
        result = capture.capture(backend, backend.identity_value, writer, 1, clock=clock,
                                 sleep=clock.sleep, follow_connections=True)
        rows = [json.loads(line) for line in stream.getvalue().splitlines()]
        self.assertEqual("capture.durationComplete", result["reason"])
        self.assertEqual(1, result["scopeChanges"])
        self.assertEqual(2, result["scopeCount"])
        self.assertEqual("discontinuous", result["connectionScopeContinuity"])
        self.assertEqual(4, result["droppedPackets"])
        self.assertEqual(1, result["rejectedReasons"]["packet.outOfScope"])
        self.assertEqual(2, len(rows))
        self.assertNotEqual(rows[0]["connectionId"], rows[1]["connectionId"])
        self.assertEqual([10, 20], [row["tcpSequence"] for row in rows])
        self.assertLess(backend.events.index(("close", 1)), backend.events.index(("open", 2)))
        self.assertEqual(backend.opened, backend.closed)
        self.assertEqual(1, result["elapsedSeconds"])
        self.assertGreaterEqual(rows[1]["monotonicMs"], 600)

    def test_remote_port_selection_ignores_auxiliary_owner_connection_churn(self):
        clock, stream = Clock(), io.BytesIO()
        backend = FakeBackend([(1, packet(), 100, None)])
        backend.connections = lambda: [backend.connection, capture.Connection("192.0.2.1", 55000,
                                      "198.51.100.1", 7000 + int(clock.now / 0.02))]
        result = capture.capture(backend, backend.identity_value, capture.EvidenceWriter(stream, 2048, 10),
                                 1, clock=clock, sleep=clock.sleep, follow_connections=True, remote_port=1111)
        self.assertEqual("capture.durationComplete", result["reason"])
        self.assertEqual(0, result["scopeChanges"])
        self.assertEqual(1, result["scopeCount"])
        self.assertEqual(1111, result["remotePort"])
        self.assertEqual(1, len(backend.filters))
        self.assertNotIn("55000", backend.filters[0])
        self.assertEqual(backend.opened, backend.closed)

    def test_selected_data_connection_changes_still_rebind_while_auxiliary_changes_are_ignored(self):
        clock, stream = Clock(), io.BytesIO()
        backend = FollowingBackend(clock)
        data_connections = backend.connections
        backend.connections = lambda: data_connections() + [capture.Connection("192.0.2.1", 55000,
                                      "198.51.100.1", 7000 + int(clock.now / 0.02))]
        result = capture.capture(backend, backend.identity_value, capture.EvidenceWriter(stream, 2048, 10),
                                 1, clock=clock, sleep=clock.sleep, follow_connections=True, remote_port=1111)
        self.assertEqual("capture.durationComplete", result["reason"])
        self.assertEqual(1, result["scopeChanges"])
        self.assertEqual(2, len(backend.filters))
        self.assertTrue(all("55000" not in expression for expression in backend.filters))
        self.assertEqual(backend.opened, backend.closed)

    def test_native_owner_table_excludes_other_pids_even_when_remote_ports_match(self):
        def row(owner, local_port, remote_port, state=5):
            data = bytearray(24)
            struct.pack_into("<I", data, 0, state)
            data[4:8], data[12:16] = bytes([192, 0, 2, 1]), bytes([198, 51, 100, 1])
            struct.pack_into("!H", data, 8, local_port)
            struct.pack_into("!H", data, 16, remote_port)
            struct.pack_into("<I", data, 20, owner)
            return data
        table = struct.pack("<I", 4) + row(11, 50000, 1111) + row(22, 50001, 1111) + row(11, 50002, 1111, 2) + row(33, 50003, 7688)
        owned = capture.owned_connections(table, 11)
        self.assertEqual([capture.Connection("192.0.2.1", 50000, "198.51.100.1", 1111)], owned)
        backend = FakeBackend([(1, packet(), 100, None)])
        backend.connections = lambda: owned
        result, rows, _ = self.run_capture(backend, remote_port=1111)
        self.assertEqual(1, result["connectionCount"])
        self.assertEqual(1, len(rows))
        self.assertNotIn("50001", backend.filters[0])

    def test_follow_mode_does_not_reset_the_shared_packet_or_byte_budget(self):
        for byte_budget in (False, True):
            with self.subTest(byte_budget=byte_budget):
                clock, stream = Clock(), io.BytesIO()
                backend = FollowingBackend(clock)
                writer = capture.EvidenceWriter(stream, 300 if byte_budget else capture.MAXIMUM_BYTES,
                                                100 if byte_budget else 1)
                result = capture.capture(backend, backend.identity_value, writer, 1, clock=clock,
                                         sleep=clock.sleep, follow_connections=True)
                self.assertEqual("capture.byteBudget" if byte_budget else "capture.packetBudget", result["reason"])
                self.assertEqual(1, writer.packets)
                self.assertEqual(2, result["scopeCount"])
                self.assertEqual(backend.opened, backend.closed)

    def test_follow_mode_still_stops_when_the_fixed_process_creation_identity_changes(self):
        backend = FakeBackend()
        backend.identity_change = 3
        result, rows, _ = self.run_capture(backend, follow_connections=True)
        self.assertEqual("capture.processChanged", result["reason"])
        self.assertEqual([], rows)
        self.assertEqual(backend.opened, backend.closed)

    def test_follow_mode_waits_for_empty_owner_table_only_until_the_original_deadline(self):
        clock, stream = Clock(), io.BytesIO()
        backend = FollowingBackend(clock, always_empty=True)
        result = capture.capture(backend, backend.identity_value, capture.EvidenceWriter(stream, 1024, 10),
                                 1, clock=clock, sleep=clock.sleep, follow_connections=True)
        self.assertEqual("capture.durationComplete", result["reason"])
        self.assertEqual(1, result["elapsedSeconds"])
        self.assertEqual(0, result["scopeChanges"])
        self.assertEqual([], backend.opened)
        self.assertEqual(b"", stream.getvalue())

    def test_follow_mode_stops_after_four_rebindings_and_closes_every_handle(self):
        clock, stream = Clock(), io.BytesIO()
        backend = FollowingBackend(clock, rapid_changes=True)
        result = capture.capture(backend, backend.identity_value, capture.EvidenceWriter(stream, capture.MAXIMUM_BYTES, 100),
                                 2, clock=clock, sleep=clock.sleep, follow_connections=True)
        self.assertEqual("capture.scopeBudget", result["reason"])
        self.assertEqual(4, result["scopeChanges"])
        self.assertEqual(5, result["scopeCount"])
        self.assertEqual(5, len(backend.opened))
        self.assertEqual(10, result["droppedPackets"])
        self.assertEqual(backend.opened, backend.closed)

    def test_loopback_device_without_addresses_is_scoped_to_the_exact_loopback_connection(self):
        backend = FakeBackend()
        backend.connection = capture.Connection("127.0.0.1", 8133, "127.0.0.1", 8134)
        backend.devices = lambda: [capture.Device(b"\\Device\\NPF_Loopback", frozenset())]
        result, _, _ = self.run_capture(backend)
        self.assertEqual("capture.durationComplete", result["reason"])
        self.assertIn("src port 8134", backend.filters[0])
        self.assertEqual(backend.opened, backend.closed)

    def test_uncovered_connections_fail_before_opening_any_device(self):
        backend = FakeBackend()
        backend.devices = lambda: []
        with self.assertRaisesRegex(capture.CaptureError, "channelUnavailable"):
            self.run_capture(backend)
        self.assertEqual([], backend.opened)

    def test_process_creation_and_executable_identity_are_both_required(self):
        expected = capture.ProcessIdentity(1, 2, "game.exe")
        for actual in (capture.ProcessIdentity(2, 2, "game.exe"), capture.ProcessIdentity(1, 3, "game.exe"),
                       capture.ProcessIdentity(1, 2, "other.exe")):
            with self.assertRaisesRegex(capture.CaptureError, "processChanged"):
                capture.same_process(expected, actual)

    def test_output_is_new_under_artifacts_tmp_and_acl_failure_removes_the_empty_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            target = root / "artifacts/tmp/capture-test"
            result = capture.prepare_output(target, root, secure=lambda _: None)
            self.assertEqual(target, result)
            for bad in (target, root / "outside", root / "artifacts/tmp"):
                with self.assertRaises(capture.CaptureError):
                    capture.output_path(bad, root)
            failure = root / "artifacts/tmp/acl-failure"
            def deny(_):
                raise capture.CaptureError("capture.outputAclFailed")
            with self.assertRaises(capture.CaptureError):
                capture.prepare_output(failure, root, secure=deny)
            self.assertFalse(failure.exists())

    def test_output_rejects_symlink_or_junction_ancestors(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "artifacts/tmp").mkdir(parents=True)
            original = capture.is_link
            capture.is_link = lambda path: path.name == "tmp" or original(path)
            try:
                with self.assertRaisesRegex(capture.CaptureError, "outputLinked"):
                    capture.output_path(root / "artifacts/tmp/run", root)
            finally:
                capture.is_link = original

    def test_windows_pcap_header_uses_two_int32_time_fields_and_caplen_at_eight(self):
        self.assertEqual(16, ctypes.sizeof(capture.PcapHeader))
        self.assertEqual(8, capture.PcapHeader.captured.offset)
        self.assertEqual(12, capture.PcapHeader.length.offset)
        header = capture.PcapHeader.from_buffer_copy(struct.pack("<iiII", 123, 456, 70000, 70000))
        self.assertEqual(70000, header.captured)

    def test_native_open_sets_receive_budget_before_activation_and_releases_failed_handles(self):
        class Bpf(ctypes.Structure):
            _fields_ = [("length", ctypes.c_uint), ("instructions", ctypes.c_void_p)]
        class Pcap:
            def __init__(self):
                self.calls = []
                self.fail_filter = False
            def __getattr__(self, name):
                def call(*args):
                    self.calls.append((name, args))
                    if name == "pcap_create":
                        return 42
                    if name == "pcap_datalink":
                        return 1
                    return -1 if name == "pcap_setfilter" and self.fail_filter else 0
                return call
        backend = capture.NativeBackend.__new__(capture.NativeBackend)
        backend.pcap, backend.Bpf = Pcap(), Bpf
        device = capture.Device(b"fixture", frozenset())
        self.assertEqual((42, 1), backend.open(device, "tcp and dst port 1"))
        names = [name for name, _ in backend.pcap.calls]
        self.assertLess(names.index("pcap_set_buffer_size"), names.index("pcap_activate"))
        self.assertIn(("pcap_set_buffer_size", (42, 16 * 1024 * 1024)), backend.pcap.calls)
        self.assertIn(("pcap_set_snaplen", (42, 262144)), backend.pcap.calls)
        backend.pcap.fail_filter = True
        with self.assertRaisesRegex(capture.CaptureError, "filterFailed"):
            backend.open(device, "tcp and dst port 1")
        self.assertEqual("pcap_close", backend.pcap.calls[-1][0])

    def test_native_capture_length_must_equal_wire_length_before_copying(self):
        class Pcap:
            header = capture.PcapHeader(123, 456, 50, 60)
            data = ctypes.create_string_buffer(b"a" * 60)
            def pcap_next_ex(self, handle, header, data):
                ctypes.cast(header, ctypes.POINTER(ctypes.POINTER(capture.PcapHeader)))[0] = ctypes.pointer(self.header)
                ctypes.cast(data, ctypes.POINTER(ctypes.c_void_p))[0] = ctypes.cast(self.data, ctypes.c_void_p)
                return 1
        backend = capture.NativeBackend.__new__(capture.NativeBackend)
        backend.pcap, backend.Header = Pcap(), capture.PcapHeader
        self.assertEqual((1, b"", None, "packet.truncatedOrOversized"), backend.read((42, 1)))

    def test_connection_ownership_is_rechecked_before_writing_a_received_packet(self):
        backend = FakeBackend([(1, packet(), 100, None)])
        backend.connection_change = 3
        result, rows, _ = self.run_capture(backend)
        self.assertEqual("capture.connectionsChanged", result["reason"])
        self.assertEqual([], rows)
        self.assertEqual(backend.opened, backend.closed)


def tcp_packet(source, source_port, destination, destination_port, payload=b"", flags=0x18, sequence=1):
    ip = bytearray(20)
    struct.pack_into("!BBHHHBBH4s4s", ip, 0, 0x45, 0, 40 + len(payload), 0, 0, 64, 6, 0,
                     bytes(map(int, source.split("."))), bytes(map(int, destination.split("."))))
    tcp = bytearray(20)
    struct.pack_into("!HHI", tcp, 0, source_port, destination_port, sequence)
    tcp[12], tcp[13] = 0x50, flags
    return struct.pack("<I", 2) + ip + tcp + payload


class TrackedBackend:
    """连接表随时间变化的回环夹具：A 一开始属于游戏；B 的首包早于连接表出现；C 从不属于游戏。"""
    identity_value = capture.ProcessIdentity(11, 100, "game.exe")
    A = capture.Connection("127.0.0.1", 50000, "127.0.0.1", 1111)
    B = capture.Connection("127.0.0.1", 50001, "127.0.0.1", 2222)
    C = capture.Connection("127.0.0.1", 50002, "127.0.0.1", 3333)

    def __init__(self, clock, b_appears_at=0.3):
        self.clock, self.b_appears_at = clock, b_appears_at
        self.opened, self.closed, self.filters = [], [], []
        self.queue = [
            (0.0, tcp_packet("127.0.0.1", 1111, "127.0.0.1", 50000, b"alpha")),
            (0.05, tcp_packet("127.0.0.1", 2222, "127.0.0.1", 50001, b"", flags=0x12, sequence=99)),
            (0.06, tcp_packet("127.0.0.1", 2222, "127.0.0.1", 50001, b"early-login", sequence=100)),
            (0.07, tcp_packet("127.0.0.1", 3333, "127.0.0.1", 50002, b"unrelated")),
            (0.4, tcp_packet("127.0.0.1", 2222, "127.0.0.1", 50001, b"later", sequence=111)),
            (0.7, tcp_packet("127.0.0.1", 1111, "127.0.0.1", 50000, b"", flags=0x11, sequence=6)),
        ]

    def identity(self):
        return self.identity_value

    def tracked_connections(self):
        rows = {}
        if self.clock.now < 0.6:
            rows[self.A] = 5
        if self.clock.now >= self.b_appears_at:
            rows[self.B] = 5
        return rows

    def devices(self):
        return [capture.Device(b"\\Device\\NPF_Loopback", frozenset()), capture.Device(b"ethernet", frozenset(["192.0.2.1"]))]

    def open(self, device, expression):
        self.opened.append(device.name)
        self.filters.append(expression)
        return device.name

    def read(self, handle):
        if self.queue and self.clock.now >= self.queue[0][0]:
            return 0, self.queue.pop(0)[1], 1000, None
        return None

    def dropped(self, handle):
        return 0

    def close(self, handle):
        self.closed.append(handle)


class TrackedCaptureTests(unittest.TestCase):
    def run_tracked(self, backend, clock, **options):
        stream = io.BytesIO()
        writer = capture.EvidenceWriter(stream, capture.MAXIMUM_BYTES, 100)
        result = capture.capture_tracked(backend, backend.identity_value, writer, 1, clock=clock, sleep=clock.sleep, **options)
        return result, [json.loads(line) for line in stream.getvalue().splitlines()]

    def test_new_connection_packets_seen_before_the_owner_table_are_flushed_and_foreign_packets_are_dropped(self):
        clock = Clock()
        backend = TrackedBackend(clock)
        result, rows = self.run_tracked(backend, clock)
        self.assertEqual("capture.durationComplete", result["reason"])
        payloads = [base64.b64decode(row["payloadBase64"]) for row in rows]
        self.assertEqual([b"alpha", b"", b"early-login", b"later", b""], payloads)
        self.assertNotIn(b"unrelated", payloads)
        connections = {entry["remotePort"]: entry for entry in result["connections"]}
        self.assertEqual({1111, 2222}, set(connections))
        login = connections[2222]
        self.assertEqual(2, login["prearmedPackets"])
        self.assertIsNotNone(login["synAckMs"])
        self.assertLess(login["firstPacketMs"], login["firstSeenTableMs"])
        self.assertEqual(3, login["downlinkPackets"])
        self.assertEqual(len(b"early-login") + len(b"later"), login["downlinkPayloadBytes"])
        first = connections[1111]
        self.assertIsNotNone(first["closedTableMs"])
        self.assertIsNotNone(first["finMs"])
        self.assertTrue(first["covered"])
        self.assertEqual("loopback", first["remoteScope"])
        ids = {row["connectionId"] for row in rows}
        self.assertEqual({first["connectionId"], login["connectionId"]}, ids)
        self.assertEqual(2, result["prearmFlushedPackets"])
        self.assertEqual(1, result["prearmDiscardedPackets"])
        text = json.dumps(rows) + json.dumps(result)
        self.assertNotIn("127.0.0.1", text)
        self.assertEqual(["tcp and (dst host 127.0.0.1)"], backend.filters)
        self.assertEqual(backend.opened, backend.closed)

    def test_prearmed_packets_older_than_the_ring_window_are_discarded(self):
        clock = Clock()
        backend = TrackedBackend(clock, b_appears_at=0.8)
        result, rows = self.run_tracked(backend, clock, ring_seconds=0.2)
        payloads = [base64.b64decode(row["payloadBase64"]) for row in rows]
        self.assertNotIn(b"early-login", payloads)
        self.assertEqual(0, {entry["remotePort"]: entry for entry in result["connections"]}[2222]["prearmedPackets"])
        self.assertGreaterEqual(result["prearmDiscardedPackets"], 3)

    def test_owned_rows_keep_transitional_states_and_other_processes_are_excluded(self):
        def row(owner, local_port, remote_port, state):
            data = bytearray(24)
            struct.pack_into("<I", data, 0, state)
            data[4:8], data[12:16] = bytes([127, 0, 0, 1]), bytes([127, 0, 0, 1])
            struct.pack_into("!H", data, 8, local_port)
            struct.pack_into("!H", data, 16, remote_port)
            struct.pack_into("<I", data, 20, owner)
            return data
        table = struct.pack("<I", 4) + row(11, 50000, 1111, 3) + row(11, 50001, 2222, 5) + row(22, 50002, 1111, 5) + row(11, 50003, 9, 2)
        rows = capture.owned_rows(table, 11)
        self.assertEqual({50000: 3, 50001: 5}, {key.local_port: value for key, value in rows.items()})

    def test_tracked_mode_rejects_invalid_ring_budgets_and_process_changes(self):
        clock = Clock()
        backend = TrackedBackend(clock)
        writer = capture.EvidenceWriter(io.BytesIO(), 1024, 10)
        with self.assertRaisesRegex(capture.CaptureError, "budgetInvalid"):
            capture.capture_tracked(backend, backend.identity_value, writer, 1, clock=clock, sleep=clock.sleep, ring_bytes=0)
        backend.identity = lambda: capture.ProcessIdentity(11, 101, "game.exe")
        with self.assertRaisesRegex(capture.CaptureError, "processChanged"):
            capture.capture_tracked(backend, TrackedBackend.identity_value, writer, 1, clock=clock, sleep=clock.sleep)


if __name__ == "__main__":
    unittest.main()
