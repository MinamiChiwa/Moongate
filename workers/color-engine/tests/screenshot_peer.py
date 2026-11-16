"""真实 Worker/原控制器的离线入口，仅注入识别样本。"""
import sys
from test_game_proxy import screenshot_scene
from mabitool_color.worker import main

if __name__ == '__main__':
    with screenshot_scene(int(sys.argv[1])):
        sys.exit(main())
