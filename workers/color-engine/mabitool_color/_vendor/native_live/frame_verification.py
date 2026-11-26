"""Structured reasons for a screenshot HEX verification that did not pass.

The verdict names one reason per run status: a covering window first, then the
card layout, then per-card OCR outcomes. A reason that concerns exactly one
card carries its 1-based region number (``hex_mismatch.2``).

``independently_verified`` is the per-frame pass rule shared by the adaptive
session gate and later visual confirmation; card rows record whether each card
was verified by text or by swatch.
"""
from .dye_visual_readiness import VisualNotReady, NativeLayoutNotReady

# Order decides the run-level reason when several cards fail differently.
REASONS = ('occluded', 'layout', 'ocr_timeout', 'hex_mismatch', 'swatch_mismatch', 'ocr_unread')
SWATCH_LIMIT = 3


class VerificationFailed(VisualNotReady):
    """Screenshot verification did not pass; ``verification`` holds the per-card evidence."""

    def __init__(self, message, verification, *, layout_diagnostic=None):
        super().__init__(message)
        self.verification = verification
        self.verification_reason = verification['code']
        self.layout_diagnostic = layout_diagnostic


def card_verified_by(frame, index, color):
    """单张卡在这一帧的核验来源：``screenshot``（读出的色码与游戏数据一致）或 ``swatch``
    （文字未读出，色块与游戏色码色差不超过 ``SWATCH_LIMIT``）；未核验为 None。"""
    sources = frame.get('hex_source')
    observed = frame.get('screenshot_hex')
    distances = frame.get('hex_swatch_distances')
    if not isinstance(sources, list) or not isinstance(observed, list) or not 0 <= index < min(len(sources), len(observed)):
        return None
    if sources[index] == 'screenshot':
        return 'screenshot' if observed[index] == color else None
    if sources[index] == 'swatch':
        distance = distances[index] if isinstance(distances, list) and index < len(distances) else None
        if observed[index] is None and type(distance) in (int, float) and distance <= SWATCH_LIMIT:
            return 'swatch'
    return None


def independently_verified(frame, colors, regions=None):
    """单帧截图核验：本局放行输入与之后的目视确认共用。

    要核对的每张卡须由色码文字或色块核验（见 ``card_verified_by``），且本帧至少一张卡由
    色码文字核验，以文字确认截图里确实是本局的色卡。``regions`` 为要核对的卡（从 0 起）；
    省略时核对全部卡，并要求帧记录的 ``screenshot_hex_verified`` 为真。
    """
    if not isinstance(frame, dict) or not isinstance(colors, (list, tuple)) or not colors:
        return False
    count = len(colors)
    sources = frame.get('hex_source')
    observed = frame.get('screenshot_hex')
    if (not isinstance(sources, list) or len(sources) != count or not isinstance(observed, list)
            or len(observed) != count
            or not any(card_verified_by(frame, i, color) == 'screenshot' for i, color in enumerate(colors))):
        return False
    if regions is None:
        if frame.get('screenshot_hex_verified') is not True:
            return False
        regions = range(count)
    return all(0 <= i < count and card_verified_by(frame, i, colors[i]) is not None for i in regions)


def _card(region, frames, expected):
    row = dict(region=region, expected_hex=expected, observed_hex=[], hex_source=[], ocr_timeouts=0, ocr_calls=0,
               ocr_empty=0, best_text=None, best_swatch_distance=None, game_swatch_distance=None,
               status='verified', verified_by=None)
    statuses = []
    for frame in frames:
        values = frame.get('screenshot_hex', frame.get('hex')) or []
        observed = values[region - 1] if region - 1 < len(values) else None
        row['observed_hex'].append(observed)
        sources = frame.get('hex_source') or []
        source = sources[region - 1] if region - 1 < len(sources) else None
        row['hex_source'].append(source)
        # 色块与游戏色码的色差，取两帧中较大者。
        distances = frame.get('hex_swatch_distances') or []
        distance = distances[region - 1] if region - 1 < len(distances) else None
        if distance is not None:
            previous = row['game_swatch_distance']
            row['game_swatch_distance'] = distance if previous is None else max(previous, distance)
        reads = frame.get('hex_read_diagnostics') or []
        read = next((item for item in reads if item.get('region') == region), {})
        for key in ('ocr_timeouts', 'ocr_calls', 'ocr_empty'):
            row[key] += int(read.get(key) or 0)
        if read.get('best_text') is not None:
            row['best_text'] = read['best_text']
            row['best_swatch_distance'] = read.get('best_swatch_distance')
        if observed is not None:
            statuses.append('verified' if expected is None or observed == expected else 'hex_mismatch')
        elif source == 'swatch':
            statuses.append('verified')
        elif read.get('ocr_timeouts'):
            statuses.append('ocr_timeout')
        elif read.get('best_swatch_distance') is not None and read['best_swatch_distance'] >= SWATCH_LIMIT:
            statuses.append('swatch_mismatch')
        else:
            statuses.append('ocr_unread')
        for conflict in frame.get('hex_conflict_reads') or []:
            if conflict.get('card_index') == region - 1 and conflict.get('swatch_accepted') is False:
                row['best_swatch_distance'] = conflict.get('swatch_distance')
    failing = [status for status in statuses if status != 'verified']
    if failing:
        row['status'] = min(failing, key=REASONS.index)
    else:
        row['verified_by'] = 'swatch' if 'swatch' in row['hex_source'] else 'screenshot'
    return row


def classify(frames, expected_hex, *, occluders=None, error=None):
    """Return ``dict(reason, code, cards, occluders, error)`` for a failed verification."""
    frames = [frame for frame in (frames or []) if isinstance(frame, dict)]
    count = len(expected_hex) if isinstance(expected_hex, (list, tuple)) else max(
        (len(frame.get('hex') or []) for frame in frames), default=0)
    cards = [_card(region, frames, expected_hex[region - 1] if isinstance(expected_hex, (list, tuple)) else None)
             for region in range(1, count + 1)] if frames else []
    if occluders:
        reason = 'occluded'
    elif isinstance(error, NativeLayoutNotReady) or not cards:
        reason = 'layout'
    else:
        reason = min((card['status'] for card in cards if card['status'] != 'verified'),
                     key=REASONS.index, default='ocr_unread')
    involved = [card['region'] for card in cards if card['status'] == reason]
    code = reason + ('.' + str(involved[0]) if len(involved) == 1 and reason != 'layout' else '')
    return dict(reason=reason, code=code, cards=cards, occluders=list(occluders or []),
                error=None if error is None else type(error).__name__ + ': ' + str(error))
