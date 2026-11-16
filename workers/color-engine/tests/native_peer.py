"""真实精准 Worker 的离线来源；保留生产运行端口及服务流程。"""
from contextlib import ExitStack
import sys

from test_game_proxy import screenshot_scene
from mabitool_color.worker import main
from mabitool_color._vendor.native_live import service, collect_dye_validation
from mabitool_color._vendor.native_live.build_adapters import TW_ADAPTER, BuildUnsupported
from unittest.mock import patch


class OfflineBackend:
    adapter = TW_ADAPTER
    def __init__(self, pid, *, expected_creation=None, **unused):
        if not expected_creation:
            raise AssertionError('缺少进程启动身份')
        if sys.argv[1] == 'unsupported':
            raise BuildUnsupported(dict(gameassembly='0'*64, metadata='0'*64, unityplayer='0'*64))
        self.pid, self.creation = pid, expected_creation
        self.closed = False

    def observe_window_context(self, *args):
        return dict(extent_one_to_one=True, window=dict(client_size_physical=(200, 120)))

    def close(self):
        self.closed = True


def collect(backend, *, check, event, **unused):
    check()
    event(dict(event='armed'))
    return dict(stop_reason='no_active_session')


if __name__ == '__main__':
    with ExitStack() as stack:
        stack.enter_context(screenshot_scene(2))
        for name in ('configure_ocr', 'prepare_timer_assets', 'prepare_hex_assets', 'probe_layout_scale', 'read_input_backend'):
            stack.enter_context(patch.object(service, name, return_value={}))
        stack.enter_context(patch.object(service, 'CurrentBuildBackend', OfflineBackend))
        stack.enter_context(patch.object(collect_dye_validation, 'collect_validation_session', collect))
        sys.exit(main())
