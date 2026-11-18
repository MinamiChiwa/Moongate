# Moongate 0.1.2 更新说明

[简体中文](release-notes-0.1.2.md) · [繁體中文](release-notes-0.1.2.zh-TW.md) · [使用说明](user-guide.md) · [返回首页](../README.md)

<!-- release-header:start -->
> **测试版提示**
>
> Moongate 仍属于测试版：各项功能已经可以使用，但可能还有问题。遇到问题或有建议，欢迎到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues) 反馈。

**这一版改了什么：** 应用内更新改为直接连接 GitHub 检查和下载，不再因为接口限流或网络原因检查失败；从这一版起，以后的更新只下载有变化的文件（增量更新），通常只有二十多 MB，不用每次下载完整包。

**下载哪个文件：** 在 [GitHub Releases](https://github.com/MinamiChiwa/Moongate/releases) 的 v0.1.2 发行下方 Assets 里，下载排在最上面的 **`Moongate-v0.1.2-Windows-x64.zip`**（约 170 MB，解压后约 430 MB）。完整解压到一个普通文件夹（例如 `D:\Moongate`），双击里面的 **`Moongate.exe`** 即可运行。

**从 0.1.0 / 0.1.1 升级：** 在设置页“更新”里点“检查更新”，“下载更新”后点“安装并重启”，这一次仍是完整下载。如果检查更新失败，请手动升级：下载上面的正式包解压到一个新文件夹，关闭 Moongate，把旧文件夹里的 `userdata` 整个移到新文件夹，再运行新的 `Moongate.exe`。升到 0.1.2 之后，以后的更新就是增量更新。

| 文件 | 用途 |
|---|---|
| `Moongate-v0.1.2-Windows-x64.zip` | 正式包，下载这一个即可 |
| `Moongate-v0.1.2-Windows-x64.zip.manifest.json` | 应用内更新使用的文件清单，不用下载 |
| `Moongate-v0.1.2-Windows-x64.zip.sha256` | 正式包的 SHA-256 校验值，用来核对下载是否完整，普通用户不用下载 |
| `Moongate-v0.1.2-Windows-x64.zip.update-from-0.1.1.zip` | 应用内更新使用的增量包，不能单独解压使用，不用下载 |
| Source code (zip / tar.gz) | GitHub 自动附带的源代码，普通用户不用下载 |
<!-- release-header:end -->

## 这一版的变化

- **检查更新直接连接 GitHub**：从 GitHub 的发行页面取得最新版本并下载，不再依赖容易被限流的接口。连不上时显示“连不上 GitHub”，没有新版本时显示“已是最新版本”，两种情况分开提示。
- **增量更新**：检查更新时，Moongate 把本机的程序文件和新版本逐个比对，内容相同的直接沿用，只下载有变化的文件。设置页会写明本次是“增量更新”还是“完整下载”以及下载大小。下载支持断点续传；新版本拼好后逐个文件核对，全部一致才会安装。
- **自动改用完整下载的情况**：跨了多个版本、本机程序文件被改动过，或增量包下载、核对失败时，自动改为完整下载，不需要手动处理。
- **安装失败自动回退、数据保留**：与之前相同，新版本启动失败时回到原版本，`userdata` 里的数据保留。

## 已知问题与限制

- 游戏开着、工具无人操作时，后台服务在部分电脑上仍占用约 0.2 个 CPU 核心，后续版本继续优化。
- 游戏窗口正在被拖动或调整大小时，在设置页手动点“开始检测”可能提示绑定已变化，等窗口停稳后再点一次即可。
- 其余已知问题见 [0.1.0 更新说明](release-notes-0.1.0.md)。

## 功能与须知

Moongate 的全部功能、使用前须知（需要自行安装 Npcap、账号风险、数据保存位置等）见 [0.1.0 更新说明](release-notes-0.1.0.md)与[使用说明](user-guide.md)；对 11 月 18 日更新后游戏的支持见 [0.1.1 更新说明](release-notes-0.1.1.md)。

- **反馈问题**：在“诊断”页导出排查包，连同问题描述与复现步骤提交到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues)。
- **支持作者**：如果 Moongate 对你有帮助，欢迎在[爱发电](https://afdian.com/a/minamichiwa)或 [Patreon](https://patreon.com/chiwaminami) 赞助作者一杯咖啡。
