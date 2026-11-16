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
        self.assertEqual(probe.call_args.args[0],
                         [str(component / 'tesseract.exe'), '--list-langs', '--tessdata-dir', str(component / 'tessdata')])
        self.assertEqual(vision.pytesseract.pytesseract.tesseract_cmd, str(component / 'tesseract.exe'))
        self.assertEqual(vision.OCR_CONFIG, f'--tessdata-dir "{component / "tessdata"}"')


if __name__ == '__main__':
    unittest.main()
