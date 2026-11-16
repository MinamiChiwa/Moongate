"""目标色集合图：调用原 palette 算法，把 PNG 写入本次请求的短期资产目录。"""
import hashlib
import io
import math
import os
from pathlib import Path
import re

from . import protocol as wire

IMAGES = ('overview', 'atlas')
MAXIMUM_PNG_BYTES = 32 * 1024 * 1024


def validate(values):
    if (not isinstance(values, dict) or set(values) != {'targetHex', 'tolerance', 'image', 'assetId'} or
            not isinstance(values['targetHex'], str) or re.fullmatch('#[0-9A-F]{6}', values['targetHex']) is None or
            values['image'] not in IMAGES or not wire.identifier(values['assetId'])):
        raise wire.ProtocolError('worker.requestInvalid')
    tolerance = values['tolerance']
    # None 表示精准 HEX：集合只有目标色本身，与原界面 schedule_palette 一致。
    if tolerance is not None and (type(tolerance) not in (int, float) or not math.isfinite(tolerance) or not 0 < tolerance <= 100):
        raise wire.ProtocolError('worker.requestInvalid')
    return values


def render(asset_root, run_id, values, cancel):
    import numpy as np
    from ._vendor.palette import allowed_colors, overview, full_atlas
    from ._vendor.vision import rgb
    values = validate(values)
    target, tolerance = values['targetHex'], values['tolerance']
    pixels = (np.array([rgb(target)], np.uint8) if tolerance is None
              else allowed_colors(target, float(tolerance)))
    if cancel.is_set(): raise InterruptedError()
    image = overview(pixels) if values['image'] == 'overview' else full_atlas(pixels)
    if cancel.is_set(): raise InterruptedError()
    buffer = io.BytesIO()
    image.save(buffer, 'PNG')
    data = buffer.getvalue()
    if len(data) > MAXIMUM_PNG_BYTES: raise wire.ProtocolError('worker.resultInvalid')
    root = Path(asset_root)
    if not root.is_absolute() or not wire.identifier(run_id): raise wire.ProtocolError('worker.assetInvalid')
    folder = root / run_id
    path = folder / (values['assetId'] + '.png')
    folder.mkdir(exist_ok=True)
    wire.reject_links(path)
    if not path.resolve().is_relative_to(root.resolve()): raise wire.ProtocolError('worker.assetInvalid')
    with open(path, 'xb') as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    wire.reject_links(path)
    return dict(colorCount=int(len(pixels)), width=int(image.width), height=int(image.height),
                sizeBytes=len(data), sha256=hashlib.sha256(data).hexdigest())
