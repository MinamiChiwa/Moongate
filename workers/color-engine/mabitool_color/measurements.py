"""把原引擎独立读取证据收敛为无图像、无进程地址的结果。"""
from datetime import datetime, timedelta, timezone
import math
import time


def snapshot(best, last, current, *, clock=time.monotonic):
    now = clock()
    utc = datetime.now(timezone.utc)
    def measured(row):
        if not row or row.get('verified') is not True:
            return None
        observed = row.get('observed_monotonic')
        if type(observed) not in (int, float) or not math.isfinite(observed) or not 0 < observed <= now:
            return None
        return dict(colors=list(row['actual_colors']), deltas=list(row['actual_deltas']),
                    observedAt=(utc-timedelta(seconds=now-observed)).isoformat(),
                    accepted=row.get('accepted') is True, targetExact=row.get('target_exact') is True)
    return dict(bestMeasured=measured(best), lastMeasured=measured(last), currentVerified=measured(current))


def region_states(colors, deltas, rules):
    """按原区域卡徽标判定：色差保留两位后对照区域规则，禁用或无色差的区域不参与。"""
    states = []
    for color, delta, rule in zip(colors, deltas, rules):
        if delta is None:
            states.append('not-started' if color is None and rule.get('enabled') else 'excluded')
        elif not rule.get('enabled'):
            states.append('disabled')
        else:
            value = float(f'{delta:.2f}')
            met = value == 0 if rule.get('exact') else value <= rule.get('tolerance', 0)
            states.append('met' if met else 'unmet')
    return states


def annotate(result):
    """为结果内的独立读取证据补区域判定；规则缺失或区域数不符时保持原样。"""
    if not isinstance(result, dict): return result
    rules, measured = result.get('rules'), result.get('measurements')
    if not isinstance(rules, list) or not isinstance(measured, dict): return result
    for row in measured.values():
        if isinstance(row, dict) and len(row.get('colors', ())) == len(rules) == len(row.get('deltas', ())):
            row['regionStates'] = region_states(row['colors'], row['deltas'], rules)
    return result


def reading_fields(colors, rules):
    """一次读取当前画面的色码：按原 actual_result 的 error 算色差并给出区域判定，没读出的区域判定为 unread。

    原截图引擎的 scene 与精准回读 native_colors 只带色码；界面的“当前／实测”与判定要用同一份读数，因此在这里一并算出。
    """
    if not isinstance(colors, list) or not colors or len(colors) > len(rules):
        return {}
    from ._vendor.vision import error
    rules = rules[:len(colors)]
    deltas = [error(color, rule['colors'], False) if rule.get('enabled') and isinstance(color, str) else None
              for color, rule in zip(colors, rules)]
    states = ['unread' if rule.get('enabled') and not isinstance(color, str) else state
              for color, rule, state in zip(colors, rules, region_states(colors, deltas, rules))]
    complete = all(isinstance(color, str) for color, rule in zip(colors, rules) if rule.get('enabled'))
    return dict(deltas=deltas, region_states=states, reading_complete=complete)


def candidate_fields(row, rules):
    """候选区域判定与色系偏离；色系按原 verify_result 的 family_priority/family_fields 计算。"""
    from ._vendor.color_family import family_priority, family_fields
    from ._vendor.vision import rgb
    colors, deltas = row.get('colors') or [], row.get('deltas') or []
    if len(colors) != len(rules) or len(deltas) != len(rules): return {}
    fields = dict(region_states=region_states(colors, deltas, rules))
    if all(colors[i] is not None for i, rule in enumerate(rules) if rule.get('enabled')) and any(r.get('enabled') for r in rules):
        family = family_priority([[rgb(colors[i])] if rule.get('enabled') else None for i, rule in enumerate(rules)], rules)
        fields['family_consistent'] = family_fields(*family, 0)['family_consistent']
    return fields
