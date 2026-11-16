"""验证按需导入、数值线程和纹理缓存边界，保留原采样结果。"""
import os
from pathlib import Path
import subprocess
import sys
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import cv2
import numpy as np
from mabitool_color._vendor import native_periodic_search as search
from mabitool_color._vendor.profile_store import default_profile
from mabitool_color.worker import Worker


def session():
    pixels = np.arange(4 * 4 * 3, dtype=np.uint8).reshape(4, 4, 3)
    return dict(pixels=(pixels,), color_preserve_ratio=.25)


class RuntimeBudgetTests(unittest.TestCase):
    def test_control_import_does_not_load_computation_or_ui(self):
        child = subprocess.run([sys.executable, '-B', '-s', '-c',
            "import sys; import mabitool_color.worker; "
            "assert not {'numpy','cv2','PySide6','PyQt6'}.intersection(sys.modules)"],
            cwd=Path(__file__).resolve().parents[1], capture_output=True, text=True)
        self.assertEqual(child.returncode, 0, child.stderr)

    def test_each_computation_uses_the_configured_opencv_budget(self):
        for limit in (1, 2, 4):
            with self.subTest(limit=limit), patch.dict(os.environ, MABITOOL_COLOR_THREADS=str(limit)):
                frame = dict(protocolVersion=1, messageId='1', requestId='a' * 32, kind='request',
                    runId='b' * 32, bindingRevision='0', fencingToken='0',
                    payload=dict(method='color.evaluate', arguments=dict(profile=default_profile(), colors=['#202020'] * 2)))
                worker = Worker(None, '', Path(__file__).resolve().parents[1])
                worker.execute(frame, threading.Event())
                self.assertEqual(cv2.getNumThreads(), limit)
                self.assertIsNone(worker.active)

    def test_oversized_preprocessing_is_used_without_retention(self):
        baseline = search.periodic_samples(session(), 0, 0., 0.)
        byte_count = sum(value.nbytes for value in baseline)
        small = session()
        with patch.object(search, 'PREPROCESS_CACHE_BYTES', byte_count - 1):
            result = search.periodic_samples(small, 0, 0., 0.)
            self.assertEqual(len(small['_periodic_sample_cache']), 0)
            repeated = search.periodic_samples(small, 0, 0., 0.)
        for expected, actual, again in zip(baseline, result, repeated):
            np.testing.assert_array_equal(actual, expected)
            np.testing.assert_array_equal(again, expected)
            self.assertFalse(actual.flags.writeable)
            self.assertIsNot(again, actual)

    def test_cache_budget_evicts_the_oldest_entry_without_changing_values(self):
        cached = session()
        first = search.periodic_samples(cached, 0, 0., 0.)
        byte_count = sum(value.nbytes for value in first)
        with patch.object(search, 'PREPROCESS_CACHE_BYTES', byte_count * 2):
            self.assertIs(search.periodic_samples(cached, 0, 0., 0.), first)
            search.periodic_samples(cached, 0, .25, 0.)
            search.periodic_samples(cached, 0, .5, 0.)
            self.assertEqual(len(cached['_periodic_sample_cache']), 2)
            self.assertLessEqual(sum(sum(value.nbytes for value in row) for row in cached['_periodic_sample_cache'].values()), byte_count * 2)
            reloaded = search.periodic_samples(cached, 0, 0., 0.)
            self.assertIsNot(reloaded, first)
            for expected, actual in zip(first, reloaded): np.testing.assert_array_equal(actual, expected)


if __name__ == '__main__': unittest.main()
