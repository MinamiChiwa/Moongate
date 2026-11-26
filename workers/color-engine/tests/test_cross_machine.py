"""精准寻色在不同电脑上的核验：核验原因、UnityPlayer 特征定位与渲染尺寸映射。"""
import hashlib
import os
from pathlib import Path
import sys
import unittest
from unittest import mock

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mabitool_color._vendor.native_input_response import InputGeometry
from mabitool_color._vendor.native_live.build_adapters import UNITY_LAYOUT
from mabitool_color._vendor.native_live.frame_verification import classify, independently_verified
from mabitool_color._vendor.native_live.map_unity_window import map_unity_geometry_to_client
from mabitool_color._vendor.native_live.project_probe_io import card_swatch, merge_native_hex
from mabitool_color._vendor.native_live.read_dye_window_mapping import render_scale_viewport
from mabitool_color._vendor.native_live import same_session_dye_planner as planner

LOCAL_UNITYPLAYER = Path(os.environ.get('MABITOOL_TEST_UNITYPLAYER', r'C:\Nexon\Library\MabinogiMobile_TW\appdata\UnityPlayer.dll'))
EXPECTED = ['#112233', '#445566', '#778899']


def frame(hexes, reads=()):
    rows = [dict(region=i + 1, ocr_calls=1, ocr_timeouts=0, ocr_empty=0, best_text=None, best_swatch_distance=None)
            for i in range(3)]
    for region, values in reads:
        rows[region - 1].update(values)
    return dict(hex=list(hexes), screenshot_hex=list(hexes), hex_read_diagnostics=rows)


class VerificationReasonTests(unittest.TestCase):
    def test_each_failure_names_its_cause_and_single_card(self):
        timeout = frame(['#112233', None, '#778899'], [(2, dict(ocr_timeouts=2))])
        self.assertEqual(classify([timeout, timeout], EXPECTED)['code'], 'ocr_timeout.2')
        mismatch = frame(['#112234', '#445566', '#778899'])
        self.assertEqual(classify([mismatch], EXPECTED)['code'], 'hex_mismatch.1')
        swatch = frame(['#112233', '#445566', None], [(3, dict(best_text='#778899', best_swatch_distance=9.5))])
        verdict = classify([swatch], EXPECTED)
        self.assertEqual(verdict['code'], 'swatch_mismatch.3')
        self.assertEqual(verdict['cards'][2]['best_swatch_distance'], 9.5)
        unread = frame([None, None, '#778899'])
        self.assertEqual(classify([unread], EXPECTED)['code'], 'ocr_unread')

    def test_a_covering_window_and_missing_layout_take_precedence(self):
        timeout = frame(['#112233', None, '#778899'], [(2, dict(ocr_timeouts=1))])
        covered = classify([timeout], EXPECTED, occluders=[dict(rect=[0, 0, 10, 10], overlap=[0, 0, 5, 5], process='x.exe')])
        self.assertEqual((covered['reason'], covered['code']), ('occluded', 'occluded'))
        self.assertEqual(classify(None, EXPECTED)['code'], 'layout')


class SwatchVerificationTests(unittest.TestCase):
    """色码文字误读而色块一致：自适应构建本局放行前的逐帧截图核验。"""
    CARDS = [(10, 10, 60, 60), (110, 10, 60, 60), (210, 10, 60, 60)]

    def merged(self, texts, swatches):
        image = np.zeros((90, 290, 3), np.uint8)
        for (x, y, w, h), color in zip(self.CARDS, swatches):
            image[y:y + h, x:x + w] = [int(color[i:i + 2], 16) for i in (1, 3, 5)]
        decoded = frame(texts, [(2, dict(best_text='#4F5566', best_swatch_distance=10.0))])
        decoded.update(cards=list(self.CARDS))
        return merge_native_hex(decoded, EXPECTED, [card_swatch(image, card) for card in self.CARDS])

    def test_misread_card_passes_by_swatch_when_another_card_text_matches(self):
        merged = self.merged(['#112233', None, '#778899'], EXPECTED)
        self.assertEqual(merged['hex_source'], ['screenshot', 'swatch', 'screenshot'])
        self.assertEqual(merged['screenshot_hex'], ['#112233', None, '#778899'])
        self.assertTrue(independently_verified(merged, EXPECTED))
        card = classify([merged, merged], EXPECTED)['cards'][1]
        self.assertEqual((card['status'], card['verified_by'], card['game_swatch_distance']), ('verified', 'swatch', 0.0))

    def test_swatch_differing_from_game_hex_stays_swatch_mismatch(self):
        merged = self.merged(['#112233', None, '#778899'], ['#112233', '#4F5566', '#778899'])
        self.assertEqual(merged['hex_source'][1], 'native_checkpoint')
        self.assertFalse(independently_verified(merged, EXPECTED))
        self.assertEqual(classify([merged, merged], EXPECTED)['code'], 'swatch_mismatch.2')

    def test_swatch_alone_on_every_card_is_not_enough(self):
        merged = self.merged([None, None, None], EXPECTED)
        self.assertEqual(merged['hex_source'], ['native_checkpoint'] * 3)
        self.assertFalse(independently_verified(merged, EXPECTED))

    def feedback(self, frames):
        """回读的目视确认（assess_feedback）；本帧色码已达标，只看截图核验。"""
        native = dict(stop_reason='target_observed', observed_target_accepted=True, screenshot_verified=False)
        rules = [dict(enabled=True, exact=True, colors=[color], tolerance=0) for color in EXPECTED]
        with mock.patch.object(planner, 'assess_native_feedback', return_value=native):
            return planner.assess_feedback(None, dict(client_hex=EXPECTED), frames, rules)

    def test_later_readback_confirms_a_swatch_verified_card(self):
        frames = [dict(self.merged(['#112233', None, '#778899'], EXPECTED), captured_monotonic=when) for when in (1., 2.)]
        self.assertTrue(all(planner.screenshot_confirmed(dict(client_hex=EXPECTED), frame) for frame in frames))
        result = self.feedback(frames)
        self.assertEqual((result['screenshot_verified'], result['observed_target_accepted']), (True, True))

    def test_later_readback_by_swatch_alone_is_unconfirmed(self):
        swatch_only = dict(hex=list(EXPECTED), screenshot_hex=[None] * 3, hex_source=['swatch'] * 3,
                           hex_swatch_distances=[0.] * 3, screenshot_hex_verified=True)
        frames = [dict(swatch_only, captured_monotonic=when) for when in (1., 2.)]
        self.assertFalse(planner.screenshot_confirmed(dict(client_hex=EXPECTED), frames[0]))
        result = self.feedback(frames)
        self.assertEqual((result['screenshot_verified'], result['stop_reason']), (False, 'target_visual_unconfirmed'))


class UnityLayoutLocatorTests(unittest.TestCase):
    @unittest.skipUnless(LOCAL_UNITYPLAYER.is_file(), 'local UnityPlayer.dll unavailable')
    def test_registered_unityplayer_locates_its_registered_offsets(self):
        from mabitool_color._vendor.native_live.unity_layout import locate_unity_layout
        digest = hashlib.sha256(LOCAL_UNITYPLAYER.read_bytes()).hexdigest()
        if digest != UNITY_LAYOUT.sha256:
            self.skipTest('local UnityPlayer.dll is not the registered build')
        located = locate_unity_layout(LOCAL_UNITYPLAYER, digest)
        self.assertEqual((located.mouse_getter_rva, located.screen_global_rva, located.viewport_offset),
                         (UNITY_LAYOUT.mouse_getter_rva, UNITY_LAYOUT.screen_global_rva, UNITY_LAYOUT.viewport_offset))
        self.assertTrue(located.located)
        self.assertIsNone(located.mouse_cache_rva)


class RenderScaleMappingTests(unittest.TestCase):
    WINDOW = dict(source='win32_client_physical', physical_coordinates=True, minimized=False, visible=True,
                  client_size_physical=[1920, 1080], client_origin_physical=[100, 50])
    SCREEN = dict(source='current_build_native_screen_fields', size=[1280, 720])

    def mapping(self, origin, scale):
        target = [1920, 1080]
        return dict(source='win32_target_awareness_pixel_lattice',
                    coordinate_validation=dict(verified=True,
                        scheme='physical_to_logical_for_hwnd_then_target_context_screen_to_client'),
                    physical_client_size=target, native_screen_size=[1280, 720], target_client_size=target,
                    target_client_x_by_physical_x=list(range(1920)), target_client_y_by_physical_y=list(range(1080)),
                    viewport_origin=origin, viewport_scale=scale, render_scale_verified=True)

    def test_viewport_must_reproduce_the_unity_render_size(self):
        self.assertIsNotNone(render_scale_viewport([1920, 1080], [1280, 720], [0, 0], [2 / 3, 2 / 3]))
        # 4:3 渲染在 16:9 客户区两侧留黑边：原点 240 像素。
        self.assertIsNotNone(render_scale_viewport([1920, 1080], [1024, 768], [240, 0], [1024 / 1440, 768 / 1080]))
        self.assertIsNone(render_scale_viewport([1920, 1080], [1280, 720], [0, 0], [1., 1.]))
        self.assertIsNone(render_scale_viewport([1920, 1080], [1280, 720], None, None))

    def test_board_and_mouse_use_the_same_viewport(self):
        geometry = dict(screen_origin='unity_bottom_left', axis_aligned_candidate=True,
                        predicted_unity_corners=[[400, 200], [600, 200], [400, 400], [600, 400]])
        mapped = map_unity_geometry_to_client(geometry, self.WINDOW, self.SCREEN,
                                              pixel_mapping=self.mapping([0, 0], [2 / 3, 2 / 3]))
        np.testing.assert_allclose(mapped['client_board_candidate'], [600, 480, 900, 780])
        board = InputGeometry(mapped['client_board_candidate'], (500., 500.), 'windows_legacy_mouse_pixels',
                              mapped['pixel_mapping'])
        # 板左上角的物理像素经视口 floor((客户区 − 原点) × 缩放) 回到 Unity 像素 400、320；
        # 纵向按原 getter 的 H−y−1 约定比上边缘低一个 Unity 像素（500/200 = 2.5 个本地单位）。
        np.testing.assert_allclose(board.local((600, 480)), [-250., 247.5])
        with self.assertRaises(ValueError):
            map_unity_geometry_to_client(geometry, self.WINDOW, self.SCREEN,
                                         pixel_mapping=self.mapping([0, 0], [1., 1.]))


if __name__ == '__main__':
    unittest.main()
