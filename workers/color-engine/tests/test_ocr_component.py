"""验证 OCR 只使用染色组件内的 Tesseract，缺失时给出稳定原因码且不回退系统安装。"""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mabitool_color._vendor import vision
from mabitool_color._vendor.native_live.service import unavailable_reason


class OcrComponentTests(unittest.TestCase):
    def setUp(self):
        self.addCleanup(setattr, vision, 'OCR_AVAILABLE', vision.OCR_AVAILABLE)
        self.addCleanup(setattr, vision, 'OCR_CONFIG', vision.OCR_CONFIG)
        for name in ('OCR_CWD', 'OCR_TIMEOUT', 'OCR_TIME_SCALE', 'OCR_WARMUP'):
            self.addCleanup(setattr, vision, name, getattr(vision, name))
        self.addCleanup(setattr, vision.pytesseract.pytesseract, 'tesseract_cmd', vision.pytesseract.pytesseract.tesseract_cmd)
        temporary = tempfile.TemporaryDirectory(prefix='ocr-component-', dir=Path(__file__).resolve().parents[3] / 'artifacts/tmp')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def test_missing_component_reports_reason_without_searching_system_installations(self):
        system = self.root / 'system'
        (system / 'tessdata').mkdir(parents=True)
        (system / 'tesseract.exe').write_bytes(b'MZ')
        (system / 'tessdata/eng.traineddata').write_bytes(b'eng')
        component = self.root / 'component'
        with patch.dict(os.environ, {vision.TESSERACT_DIRECTORY_VARIABLE: str(component),
                                     'PATH': str(system), 'TESSERACT_HOME': str(system)}), \
                patch.object(vision, '_ocr_probe') as probe:
            with self.assertRaises(vision.OcrUnavailable) as raised:
                vision.configure_ocr(strict=True)
            self.assertEqual(vision._tesseract(None, ''), '')
        probe.assert_not_called()
        self.assertEqual(unavailable_reason(raised.exception), 'ocr.componentMissing')
        self.assertIn('文字识别组件缺失', str(raised.exception))
        self.assertIs(vision.OCR_AVAILABLE, False)

    def test_component_executable_and_data_are_used_explicitly(self):
        component = self.root / 'tesseract'
        (component / 'tessdata').mkdir(parents=True)
        (component / 'tesseract.exe').write_bytes(b'MZ')
        (component / 'tessdata/eng.traineddata').write_bytes(b'eng')
        listed = subprocess.CompletedProcess([], 0, b'List of available languages (1):\r\neng\r\n', b'')
        with patch.dict(os.environ, {vision.TESSERACT_DIRECTORY_VARIABLE: str(component)}), \
                patch.object(vision, '_ocr_probe', return_value=listed) as probe:
            self.assertTrue(vision.configure_ocr(strict=True))
        # 组件目录作为工作目录、tessdata 相对传入：参数里不出现工具根路径。
        self.assertEqual(probe.call_args_list[0].args[0],
                         [str(component / 'tesseract.exe'), '--list-langs', '--tessdata-dir', 'tessdata'])
        self.assertEqual(probe.call_args_list[0].kwargs['cwd'], str(component))
        self.assertEqual(vision.pytesseract.pytesseract.tesseract_cmd, str(component / 'tesseract.exe'))
        self.assertEqual(vision.OCR_CONFIG, '--tessdata-dir tessdata')
        self.assertEqual(vision.OCR_CWD, str(component))


    def test_timeout_is_retried_once_and_counted_apart_from_empty_reads(self):
        vision.OCR_AVAILABLE, vision.OCR_TIMEOUT = True, 2.
        def delta(before):
            after = vision.ocr_counters()
            return after[0] - before[0], after[2] - before[2]
        allowances = []
        def slow_then_text(image, **options):
            allowances.append(options['timeout'])
            if len(allowances) == 1: raise subprocess.TimeoutExpired('tesseract', options['timeout'])
            return '#A1B2C3\n'
        before = vision.ocr_counters()
        with patch.object(vision, '_ocr_text', side_effect=slow_then_text):
            self.assertEqual(vision._tesseract(None, ''), '#A1B2C3')
        self.assertEqual(allowances, [2., 4.])
        self.assertEqual(delta(before), (1, 0))
        before = vision.ocr_counters()
        with patch.object(vision, '_ocr_text', side_effect=subprocess.TimeoutExpired('tesseract', 2)):
            self.assertEqual(vision._tesseract(None, ''), '')
        self.assertEqual(delta(before), (2, 0))
        before = vision.ocr_counters()
        with patch.object(vision, '_ocr_text', return_value='  '):
            self.assertEqual(vision._tesseract(None, ''), '')
        self.assertEqual(delta(before), (0, 1))


if __name__ == '__main__':
    unittest.main()
