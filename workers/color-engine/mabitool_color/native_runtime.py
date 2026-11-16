"""精准数据源接入；只读进程身份由宿主授予，截图与输入复用统一代理。"""
from pathlib import Path
from types import SimpleNamespace
import time

from ._vendor.platform_win import Interrupted
from .protocol import uint64


class NativeSourceUnavailable(ValueError):
    def __init__(self, reason, message):
        self.capability_reason = reason
        super().__init__(message)


class ProxyNativeRuntime:
    def __init__(self, game):
        self.game = game
        self.grant = None

    def check(self):
        self.game.check()

    def resolve_target(self, target, *, server=None, check):
        check()
        try:
            grant = self.game._call('game.nativeAccess', {})
        except Interrupted as exc:
            if str(exc) == 'permission.missing':
                raise NativeSourceUnavailable('permission.missing', '精准读取未获宿主授权。') from exc
            raise
        if (grant.get('pid') != self.game.target.pid or str(grant.get('hwnd')) != str(self.game.hwnd)
                or grant.get('server') not in ('tw', 'kr', 'jp')
                or not uint64(grant.get('processCreationTime')) or grant['processCreationTime'] == '0'):
            raise NativeSourceUnavailable('gameBinding.invalid', '精准授权与固定窗口绑定不符。')
        if target is not None or server not in (None, grant['server']):
            raise NativeSourceUnavailable('gameBinding.invalid', '精准运行不能替换宿主绑定。')
        self.grant = grant
        return SimpleNamespace(title=self.game.target.title, pid=grant['pid'], hwnd=self.game.hwnd)

    def activate_target(self, target, *, check):
        check()
        self.game.focus()
        return True

    def prepare(self, pid, folder, check, hwnd):
        if self.grant is None or (pid, hwnd) != (self.grant['pid'], self.game.hwnd):
            raise NativeSourceUnavailable('gameBinding.invalid', '精准读取缺少匹配的授权。')
        # 只在精准授权之后加载原生模块；截图路径不导入或构造进程 Reader。
        from ._vendor.native_live.service import _prepare
        from ._vendor.native_live.build_adapters import BuildUnsupported
        from ._vendor.native_live.native_provider_backend import ProcessBindingChanged
        backend = None
        try:
            backend, record = _prepare(pid, folder, check, hwnd,
                expected_creation=int(self.grant['processCreationTime']))
            self.enable_server_input(backend, self.grant['server'])
            check()
            return backend, record
        except BuildUnsupported as exc:
            raise NativeSourceUnavailable('gameBuild.incompatible', str(exc)) from exc
        except ProcessBindingChanged as exc:
            raise NativeSourceUnavailable('input.bindingChanged', str(exc)) from exc
        except BaseException:
            if backend is not None:
                backend.close()
            raise

    def enable_server_input(self, backend, server):
        from ._vendor.native_live.adaptive_build import is_adaptive
        from ._vendor.native_live.build_adapters import SERVER_BUILD_ADAPTERS, enable_selected_server_input
        if self.grant is None or server != self.grant['server']:
            raise NativeSourceUnavailable('gameBinding.invalid', '服务器与宿主绑定不符。')
        if is_adaptive(backend.adapter):
            # 原引擎仍须用本局独立截图证据授权输入，文件发现不直接授权输入。
            enable_selected_server_input(backend, server)
        elif backend.adapter not in SERVER_BUILD_ADAPTERS.get(server, ()):
            raise NativeSourceUnavailable('gameBuild.incompatible', '服务器与精准构建不符。')
        else:
            self.require_input_capability(backend)

    def require_input_capability(self, backend):
        from ._vendor.native_live.build_adapters import require_input_capability
        require_input_capability(backend)

    def collect(self, backend, **options):
        from ._vendor.native_live.collect_dye_validation import collect_validation_session
        return collect_validation_session(backend, **options)

    def load_session(self, folder):
        from ._vendor.native_palette_scoring import load_session
        return load_session(folder)

    def create_io(self, backend, baseline, folder, *, stop):
        from ._vendor.native_live.project_closed_loop_io import ProjectClosedLoopIO
        io = ProjectClosedLoopIO(backend, baseline, Path(folder), game=self.game,
                                stop=stop, f9_pressed=lambda: False)
        # 原控制器收紧阶段期限时，宿主收到的手势预算同步收紧。
        self.game.bind_budget(SimpleNamespace(remaining=lambda: max(0., min(
            io.deadline, io.accepted_session_deadline) - time.monotonic())))
        return io
