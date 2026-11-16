"""Color Studio 的无界面引擎入口。"""
__all__ = ['ColorEngineContext']


def __getattr__(name):
    if name == 'ColorEngineContext':
        from .context import ColorEngineContext
        return ColorEngineContext
    raise AttributeError(name)
