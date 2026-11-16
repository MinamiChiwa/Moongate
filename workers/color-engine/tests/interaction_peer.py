"""真实 Worker 管道的离线交互端口；颜色样本由本文件明确生成。"""
import sys
import time
import os
from datetime import datetime, timezone
from unittest.mock import patch
from test_game_proxy import screenshot_scene
from mabitool_color.worker import main


def run(owner, *args, **kwargs):
    deadline = time.monotonic() + 30
    epoch = 0
    def publish():
        owner.event('native_candidate_ready', batch_id=f'board-{epoch}', effective_deadline=deadline, region_count=2,
            current_candidate_id=None,
            candidates=[dict(id='choice', available=True, colors=['#202020', '#202020'], deltas=[0., 0.],
                predicted=True, current=False, accepted=True, target_exact=True)])
        owner.event('native_retry_available', enabled=True, effective_deadline=deadline)
    publish()
    while time.monotonic() < deadline:
        if owner.stop.wait(.01):
            raise InterruptedError()
        selected = owner.consume_candidate_choice(f'board-{epoch}', deadline)
        if selected is not None:
            owner.event('native_candidate_selected', batch_id=f'board-{epoch}', candidate_id=selected,
                status='observed', verified=True, actual_colors=['#202020', '#202020'])
            if len(sys.argv) > 1 and sys.argv[1] == 'complete':
                measured = dict(colors=['#202020', '#202020'], deltas=[0., 0.],
                    observedAt=datetime.now(timezone.utc).isoformat(), accepted=True, targetExact=True)
                return dict(region_count=2, outcome='matched', stop_reason='target_matched',
                    measurements=dict(bestMeasured=measured, lastMeasured=measured, currentVerified=measured))
            publish()
        if owner.consume_retry_request():
            if len(sys.argv) > 1 and sys.argv[1] == 'crash':
                os._exit(17)
            epoch += 1
            owner.event('native_retry_reset', status='searching', effective_deadline=deadline)
            publish()
    return dict(stop_reason='insufficient_time')


if __name__ == '__main__':
    # 真实原控制器的截图交互测试使用 screenshot_peer；本端口测试命令竞态。
    with screenshot_scene(2), patch('mabitool_color._vendor.screenshot_live.run_screenshot_search', side_effect=run):
        sys.exit(main())
