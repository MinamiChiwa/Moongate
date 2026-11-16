"""目标色集合图与区域判定：像素哈希取自原 Color Studio 固定提交的 palette.py 输出。"""
import hashlib
from pathlib import Path
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mabitool_color import measurements, palette_render
from mabitool_color import protocol as p

RUN = 'c' * 32

# 原工具 5e53ec8 的 allowed_colors/overview/full_atlas 对同组参数的颜色总数与 RGB 像素 SHA-256。
ORIGINAL = (
    ('#7F3F9A', None, 1, '240x64:f5e0ca051508a990e56115e457489d1cee307d5bf68a4b487480d5689cc2d395',
     '240x150:9002e9e2009b6f8327d1d51fc84a7581e116cee92323f80ac969c56d81c19055'),
    ('#FFFFFF', 1.0, 16, '240x64:a2e72a3bd96f5c3e95ff2ed0bb0dc0ad334ccd0a0f65078a85d32a45159717a9',
     '240x150:afb464cddb077c9a1633ecf248c4a60c2b0645bc4afe1b03c1dfed62acbbbc22'),
    ('#7F3F9A', 3.0, 1497, '240x64:12ffafddc73697120c8dbafd616bc38bec11b9702332c8df2fff42e6b333abfd',
     '240x150:cf28ed490fde389d9b2015dfd539a48f72724bf0296a3b9110b75d3606b0535e'),
    ('#2A6F3B', 12.0, 122541, '240x64:937c7552c98ecfcc742a9027b3f60bb695dbc7f46bd2c3e2fd37bb7fd9c89747',
     '443x277:435079268133599ca2454ed27777ae63894175c347a2374b8627f7981053ac9c'),
    ('#808080', 30.0, 1444504, '240x64:2793352bd505a60c675b516e8e4001ef7e1ac9515fc4dc55373c46554d2eb91b',
     '1521x950:802aa7a31360b2e5c6718705b54a35f418feef48c4a2d44a29df817dc2c4d377'),
)


def pixels(path):
    import numpy as np
    from PIL import Image
    with Image.open(path) as image:
        return f'{image.width}x{image.height}:' + hashlib.sha256(np.asarray(image.convert('RGB')).tobytes()).hexdigest()


class PaletteTests(unittest.TestCase):
    def test_rendered_png_matches_original_pixels(self):
        with tempfile.TemporaryDirectory() as root:
            for index, (target, tolerance, count, overview, atlas) in enumerate(ORIGINAL):
                for image, expected in (('overview', overview), ('atlas', atlas)):
                    with self.subTest(target=target, tolerance=tolerance, image=image):
                        asset = f'{index:02x}{len(image):02x}'.ljust(32, 'a')
                        result = palette_render.render(root, RUN, dict(targetHex=target, tolerance=tolerance,
                                                                          image=image, assetId=asset), threading.Event())
                        path = Path(root, RUN, asset + '.png')
                        data = path.read_bytes()
                        self.assertEqual(result['colorCount'], count)
                        self.assertEqual((result['sizeBytes'], result['sha256']), (len(data), hashlib.sha256(data).hexdigest()))
                        self.assertEqual(f"{result['width']}x{result['height']}:" + expected.split(':')[1], expected)
                        self.assertEqual(pixels(path), expected)

    def test_invalid_arguments_and_existing_output_are_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            for values in (dict(targetHex='#7f3f9a', tolerance=3, image='overview', assetId='a' * 32),
                           dict(targetHex='#7F3F9A', tolerance=0, image='overview', assetId='a' * 32),
                           dict(targetHex='#7F3F9A', tolerance=3, image='full', assetId='a' * 32),
                           dict(targetHex='#7F3F9A', tolerance=3, image='overview', assetId='../' + 'a' * 29),
                           dict(targetHex='#7F3F9A', tolerance=3, image='overview')):
                with self.subTest(values=values), self.assertRaises(p.ProtocolError):
                    palette_render.render(root, RUN, values, threading.Event())
            values = dict(targetHex='#7F3F9A', tolerance=None, image='overview', assetId='b' * 32)
            palette_render.render(root, RUN, values, threading.Event())
            with self.assertRaises(FileExistsError):
                palette_render.render(root, RUN, values, threading.Event())


class RegionStateTests(unittest.TestCase):
    def test_states_follow_original_badge_rule(self):
        rules = [dict(enabled=True, exact=False, tolerance=5, colors=['#000000']),
                 dict(enabled=True, exact=True, tolerance=0, colors=['#000000']),
                 dict(enabled=False, exact=True, tolerance=0, colors=[])]
        # 5.004 显示为 5.00，原徽标按显示值判定达标；精准 HEX 只在色差为 0 时达标。
        self.assertEqual(measurements.region_states(['#010101', '#000000', '#FFFFFF'], [5.004, 0., None], rules),
                         ['met', 'met', 'excluded'])
        self.assertEqual(measurements.region_states(['#010101', '#010101', None], [5.006, .3, None], rules),
                         ['unmet', 'unmet', 'excluded'])
        self.assertEqual(measurements.region_states([None, '#000000'], [None, 0.], rules[:2]), ['not-started', 'met'])
        self.assertEqual(measurements.region_states(['#000000'], [0.], [rules[2]]), ['disabled'])

    def test_annotate_and_candidate_fields(self):
        rules = [dict(enabled=True, exact=False, tolerance=5, colors=['#FF0000']),
                 dict(enabled=False, exact=True, tolerance=0, colors=[])]
        row = dict(colors=['#FE0000', None], deltas=[.5, None])
        result = measurements.annotate(dict(rules=rules, measurements=dict(bestMeasured=dict(row), lastMeasured=None)))
        self.assertEqual(result['measurements']['bestMeasured']['regionStates'], ['met', 'excluded'])
        fields = measurements.candidate_fields(row, rules)
        self.assertEqual(fields['region_states'], ['met', 'excluded'])
        self.assertIs(fields['family_consistent'], True)
        far = measurements.candidate_fields(dict(colors=['#00FF00', None], deltas=[80., None]), rules)
        self.assertIs(far['family_consistent'], False)
        self.assertEqual(measurements.candidate_fields(dict(colors=['#FE0000'], deltas=[.5]), rules), {})


if __name__ == '__main__':
    unittest.main()
