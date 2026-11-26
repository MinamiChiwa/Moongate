# 第三方声明与致谢

Moongate 以 [MIT 许可](LICENSE)发布。本文件列出 Moongate 移植或并入的来源项目，以及构建和发行包中使用的主要第三方组件。每次发行构建会在包内 `components/core/licenses` 生成随包组件的许可文本与 SPDX 清单，以包内文件为准。

## 来源项目

| 项目 | 作者 | 许可 | 在 Moongate 中的用途 |
|---|---|---|---|
| [MobiFolio](https://github.com/Mobi-Lan/MobiFolio) | 란님 | MIT | 乐谱名称规则、乐谱列表与演奏流程，许可原文保留在 [LICENSE](LICENSE) |
| [MabiSync](https://github.com/MinamiChiwa/MabiSync) | MinamiChiwa（南千和） | MIT（作者授权） | 合奏房间协议、校时与调度、前台输入、台服音乐与合奏观测 |
| [SyncCue](https://github.com/MinamiChiwa/SyncCue) | MinamiChiwa（南千和） | MIT（作者授权） | 房间生命周期与单调计时 |
| [Color Studio](https://github.com/MinamiChiwa/mabinogi-mobile-color-studio) | MinamiChiwa（南千和） | MIT（作者授权） | 染色寻色引擎、截图与精准数据源、选色方案 |
| MabiAgent | MinamiChiwa（南千和） | MIT（作者授权） | 背包读取与客户端道具名称关联、后台触控输入、原地专注钓鱼（状态机、进度条读数与模板匹配、PrintWindow 截图）、采集物的生活技能归类 |

战斗统计（伤害、DPS、治疗、效果、破防）由本项目依据台服实际数据与客户端资料独立实现；mobilay 的功能形态作为界面参考。

## 功能参考

下列项目只作为功能行为的参考，Moongate 不包含其代码、文字、图片或视频资源。

| 项目 | 许可 | 版权人 | 参考提交 | 参考内容 |
|---|---|---|---|---|
| [mofo-mabinogi-mobile-addon](https://github.com/sallyHADEV/mofo-mabinogi-mobile-addon) | MIT | Copyright (c) 2026 sallyHADEV | `dc6949259cea12de40d637f77acc8ca019796175` | 游戏命令行相关功能的呈现行为：任务列表排序与已完成未领奖、加工队列按设施分组与倒计时、附近合奏提示 |

## .NET 组件

| 组件 | 版本 | 许可 |
|---|---|---|
| .NET 运行时与 ASP.NET Core（随包） | 10.0 | MIT |
| Microsoft.Data.Sqlite | 10.0.13 | MIT |
| SQLitePCLRaw | 2.1.13 | Apache-2.0 |
| SQLite | 随 SQLitePCLRaw | Public Domain |
| Avalonia、Avalonia.Desktop、Avalonia.Themes.Fluent | 12.1.6 | MIT |
| Avalonia.Angle.Windows.Natives（ANGLE 预编译库） | 2.1.27548.20260419 | BSD-3-Clause（ANGLE Project Authors） |

测试项目另使用 xUnit、xunit.v3、Avalonia.Headless.XUnit 与 Microsoft.NET.Test.Sdk，均不进入发行包。

## 染色组件

染色引擎在随包的 Python 运行环境中执行，Python 依赖版本固定在 `workers/color-engine/requirements.txt`：

| 组件 | 版本 | 许可 |
|---|---|---|
| CPython（随包） | 3.12 | PSF-2.0 |
| NumPy | 2.5.2 | BSD-3-Clause |
| opencv-python | 4.14.0.94 | Apache-2.0（wheel 内附组件许可见包内说明） |
| SciPy | 1.18.1 | BSD-3-Clause |
| Pillow | 11.3.0 | MIT-CMU |
| pytesseract | 0.3.13 | Apache-2.0 |
| packaging | 26.3 | Apache-2.0 OR BSD-2-Clause |
| pefile | 2024.8.26 | MIT |
| capstone | 5.0.9 | BSD-3-Clause |

文字识别随包附带 Tesseract OCR 5.4.0.20240606（Apache-2.0，UB Mannheim Windows 构建）与英文识别数据，以及它依赖的 Leptonica、libarchive、libjpeg-turbo、libpng、libtiff、libwebp、OpenJPEG、OpenSSL、zlib、Zstandard、GNU libiconv、GCC 运行库等。各组件的版本、来源与许可登记在 `workers/color-engine/tesseract-lock.json`，原文随包放在染色组件的许可目录中。随包的 `tesseract.exe` 与 `libtesseract-5.dll` 移除了调试信息（`.debug_*` 节、COFF 符号表与随之失效的签名），代码与数据未改动。

## 界面资源

| 资源 | 许可 |
|---|---|
| Noto Sans SC、Noto Sans TC（子集化） | SIL Open Font License 1.1，原文见 `frontends/desktop/src/MabiTool.Desktop/Assets/Fonts/OFL.txt` |

## 游戏命令行工具

本仓库的 `packaging/game-cli/` 与每个发行包都附带 Mabinogi Mobile 游戏客户端自带的命令行工具 `MabinogiMobile_CLI.exe`（版本 0.1.0+b587207e73fabc7e8993d2e4d38c803f87e013f7，SHA-256 `913a4c38a0275ced1b5925a2d8e14dbefb0595c045d2590e5c04295ca8748cc8`），位于 `components/game-cli/`，由 Moongate 部署到用户绑定的台服游戏目录后调用。该文件的版权归 Nexon / devCAT 所有，不适用 Moongate 的 MIT 许可；Moongate 是非商业的爱好者项目，与 NEXON、devCAT 无关，按原样随仓库与发行包分发，未修改该文件。来源、版本、长度与哈希登记在 `packaging/game-cli/game-cli.lock.json`。

## 钓鱼识别模板

本仓库的 `workers/life-vision/Assets/templates/`（8 张）与 `tests/Providers/MabiTool.LifeVision.Tests/Fixtures/` 的测试夹具，以及每个发行包的 `components/life-vision/templates/`，是台服 Mabinogi Mobile 客户端的界面裁图（上钩图标、弹窗标题、场景图标、满包提示等），经 MabiAgent 从游戏截图裁取。这些图片的版权归 Nexon / devCAT 所有，不适用 Moongate 的 MIT 许可；Moongate 是非商业的爱好者项目，与 NEXON、devCAT 无关，按原样随仓库与发行包分发，只用于识别游戏界面。每张图片的来源、长度与 SHA-256 登记在 `templates.lock.json` 与 `fixtures.lock.json`。

## 游戏图标

本仓库的 `frontends/desktop/src/MabiTool.Desktop/Assets/GameIcons/` 与每个发行包的桌面前端（程序集内嵌资源）附带 100 个 64×64 图标，用于 DPS检测页与浮窗的属性标签（暴击、强击、连击、追击、无防备、破防、多重打击与八种元素），以及角色页、背包页的卡片。其中 96 个由 `scripts/maintenance/GameIconExtractor` 从台服 Mabinogi Mobile 客户端只读取出，版权归 Nexon / devCAT 所有，不适用 Moongate 的 MIT 许可；Moongate 是非商业的爱好者项目，与 NEXON、devCAT 无关。另 4 个（服务器、日常日历、多云、雾）由 Moongate 按客户端图标风格自绘，随项目以 MIT 许可发布。每个图标的来源资源路径、长度、SHA-256 与版权方（或“自绘”）登记在 `game-icons.lock.json`，发行包把该登记文件放在 `components/core/licenses/`。

## 不随包发行的外部组件

| 组件 | 说明 |
|---|---|
| [Npcap](https://npcap.com/) | 抓包所需，由用户自行从官网安装，适用 Npcap 自身的许可 |
| [cloudflared](https://github.com/cloudflare/cloudflared) | 合奏的 Cloudflare 公网方式使用，需另行配置，Apache-2.0 |

## 开发工具

契约测试使用 TypeScript 5.9.3（Apache-2.0）、Ajv 8.20.0（MIT）与 ajv-formats 3.0.1（MIT），锁定在 `tests/Contracts/TypeScript/package-lock.json`，不进入发行包。

《瑪奇 Mobile》及相关名称、商标归其各自权利人所有。Moongate 与 NEXON、devCAT 无关。
