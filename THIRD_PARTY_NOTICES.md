# 第三方声明与致谢

Moongate 以 [MIT 许可](LICENSE)发布。本文件列出 Moongate 移植或并入的来源项目，以及构建和发行包中使用的主要第三方组件。每次发行构建会在包内 `components/core/licenses` 生成随包组件的许可文本与 SPDX 清单，以包内文件为准。

## 来源项目

| 项目 | 作者 | 许可 | 在 Moongate 中的用途 |
|---|---|---|---|
| [MobiFolio](https://github.com/Mobi-Lan/MobiFolio) | 란님 | MIT | 乐谱名称规则、乐谱列表与演奏流程，许可原文保留在 [LICENSE](LICENSE) |
| [MabiSync](https://github.com/MinamiChiwa/MabiSync) | MinamiChiwa（南千和） | MIT（作者授权） | 合奏房间协议、校时与调度、前台输入、台服音乐与合奏观测 |
| [SyncCue](https://github.com/MinamiChiwa/SyncCue) | MinamiChiwa（南千和） | MIT（作者授权） | 房间生命周期与单调计时 |
| [Color Studio](https://github.com/MinamiChiwa/mabinogi-mobile-color-studio) | MinamiChiwa（南千和） | MIT（作者授权） | 染色寻色引擎、截图与精准数据源、选色方案 |
| MabiAgent | MinamiChiwa（南千和） | MIT（作者授权） | 背包读取与客户端道具名称关联、后台触控输入 |

战斗统计（伤害、DPS、治疗、效果、破防）由本项目依据台服实际数据与客户端资料独立实现；mobilay 的功能形态作为界面参考。

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

## 不随包发行的外部组件

| 组件 | 说明 |
|---|---|
| [Npcap](https://npcap.com/) | 抓包所需，由用户自行从官网安装，适用 Npcap 自身的许可 |
| [cloudflared](https://github.com/cloudflare/cloudflared) | 合奏的 Cloudflare 公网方式使用，需另行配置，Apache-2.0 |

## 开发工具

契约测试使用 TypeScript 5.9.3（Apache-2.0）、Ajv 8.20.0（MIT）与 ajv-formats 3.0.1（MIT），锁定在 `tests/Contracts/TypeScript/package-lock.json`，不进入发行包。

《瑪奇 Mobile》及相关名称、商标归其各自权利人所有。Moongate 与 NEXON、devCAT 无关。
