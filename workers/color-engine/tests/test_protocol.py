"""协议边界和资产授权验证；无需数值库或 GUI。"""
import hashlib
import io
import json
from pathlib import Path
import struct
import sys
import tempfile
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mabitool_color import protocol as p


class Fragmented(io.BytesIO):
    def read(self, n=-1): return super().read(min(n, 3))


class ProtocolTests(unittest.TestCase):
    def frame(self):
        return dict(protocolVersion=1, messageId='1', requestId=None, runId=None,
                    bindingRevision=None, fencingToken=None, kind='ping', payload={})

    def test_fragmented_round_trip(self):
        self.assertEqual(p.read(Fragmented(p.encode(self.frame()))), self.frame())

    def test_oversized_zero_truncated_and_duplicate_frames_rejected(self):
        for content in (struct.pack('<I', p.MAX_FRAME + 1), bytes(4), b'\x05\0', struct.pack('<I', 5) + b'{}'):
            with self.subTest(content=content), self.assertRaises((p.ProtocolError, EOFError)):
                p.read(io.BytesIO(content))
        raw = json.dumps(self.frame()).replace('"payload": {}', '"payload": {"a":1,"a":2}').encode()
        with self.assertRaises(p.ProtocolError): p.read(io.BytesIO(struct.pack('<I', len(raw)) + raw))

    def test_unknown_fields_noncanonical_ids_and_scope_mismatch_rejected(self):
        for key, value in [('extra', 1), ('messageId', '01'), ('messageId', '18446744073709551616'),
                           ('protocolVersion', True), ('requestId', 'a' * 32), ('kind', 'unknown')]:
            with self.subTest(key=key, value=value), self.assertRaises(p.ProtocolError):
                p.encode(dict(self.frame(), **{key: value}))

    def test_deep_and_nonfinite_payloads_rejected(self):
        frame = self.frame()
        data = frame['payload']
        for i in range(25): data['x'] = {}; data = data['x']
        with self.assertRaises(p.ProtocolError): p.encode(frame)
        with self.assertRaises(ValueError): p.encode(dict(self.frame(), payload=dict(value=float('nan'))))

    def test_run_scoped_asset_size_hash_expiration_and_links(self):
        with tempfile.TemporaryDirectory() as area:
            root = Path(area)
            run = 'a' * 32
            folder = root / run
            folder.mkdir()
            asset = 'b' * 32
            data = b'{"synthetic":true}'
            target = folder / (asset + '.blob')
            target.write_bytes(data)
            reference = dict(assetId=asset, runId=run, sizeBytes=len(data), sha256=hashlib.sha256(data).hexdigest(),
                             expiresAtUnixMs=int(time.time() * 1000) + 60_000)
            self.assertEqual(p.read_asset(root, reference, run), data)
            for key, value in [('runId', 'c' * 32), ('assetId', '../outside'), ('sizeBytes', len(data) + 1),
                               ('sha256', '0' * 64), ('expiresAtUnixMs', 1), ('extra', 1)]:
                with self.subTest(key=key), self.assertRaises(p.ProtocolError):
                    p.read_asset(root, dict(reference, **{key: value}), run)
            with self.assertRaises(p.ProtocolError): p.read_asset(root, reference, run, maximum=1)
            link = root / 'link'
            try: link.symlink_to(folder, target_is_directory=True)
            except OSError: return
            with self.assertRaises(p.ProtocolError): p.reject_links(link)


if __name__ == '__main__': unittest.main()
