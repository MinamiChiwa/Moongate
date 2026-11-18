"""Structured reasons for a screenshot HEX verification that did not pass.

The verdict names one reason per run status: a covering window first, then the
card layout, then per-card OCR outcomes. A reason that concerns exactly one
card carries its 1-based region number (``hex_mismatch.2``).
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


def _card(region, frames, expected):
    row = dict(region=region, expected_hex=expected, observed_hex=[], ocr_timeouts=0, ocr_calls=0,
               ocr_empty=0, best_text=None, best_swatch_distance=None, status='verified')
    statuses = []
    for frame in frames:
        values = frame.get('screenshot_hex', frame.get('hex')) or []
        observed = values[region - 1] if region - 1 < len(values) else None
        row['observed_hex'].append(observed)
        reads = frame.get('hex_read_diagnostics') or []
        read = next((item for item in reads if item.get('region') == region), {})
        for key in ('ocr_timeouts', 'ocr_calls', 'ocr_empty'):
            row[key] += int(read.get(key) or 0)
        if read.get('best_text') is not None:
            row['best_text'] = read['best_text']
            row['best_swatch_distance'] = read.get('best_swatch_distance')
        if observed is not None:
            statuses.append('verified' if expected is None or observed == expected else 'hex_mismatch')
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
