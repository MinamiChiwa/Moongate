# Moongate 0.1.6 更新说明

[简体中文](release-notes-0.1.6.md) · [繁體中文](release-notes-0.1.6.zh-TW.md) · [使用说明](user-guide.md) · [返回首页](../README.md)

<!-- release-header:start -->
> **测试版提示**
>
> Moongate 仍属于测试版：各项功能已经可以使用，但可能还有问题。遇到问题或有建议，欢迎到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues) 反馈。

**这一版的重点：** 修复增量更新意外下载完整包、下载方式与大小显示不一致，以及安装重启必须手动关闭界面的问题。

**下载哪个文件：** 在 [GitHub Releases](https://github.com/MinamiChiwa/Moongate/releases) 的 v0.1.6 发行下方 Assets 里，下载排在最上面的 **`Moongate-v0.1.6-Windows-x64.zip`**（约 170 MB，解压后约 430 MB）。完整解压到一个普通文件夹（例如 `D:\Moongate`），双击里面的 **`Moongate.exe`** 即可运行。

**从 0.1.5 或更早版本升级：** 本次升级仍使用旧版更新器，可能完整下载并在安装时需要手动关闭界面。建议下载正式包，关闭 Moongate 后，用新包的 `Moongate.exe`、`components` 与 `licenses` 替换原文件夹里的同名三项，保留 `userdata`。本版修复会在运行 0.1.6 后的后续更新中生效。

| 文件 | 用途 |
|---|---|
| `Moongate-v0.1.6-Windows-x64.zip` | 正式包，下载这一个即可 |
| `Moongate-v0.1.6-Windows-x64.zip.manifest.json` | 应用内更新使用的文件清单，不用下载 |
| `Moongate-v0.1.6-Windows-x64.zip.sha256` | 正式包的 SHA-256 校验值，用来核对下载是否完整，普通用户不用下载 |
| Source code (zip / tar.gz) | GitHub 自动附带的源代码，普通用户不用下载 |
<!-- release-header:end -->

## 这一版的变化

- **增量更新保持按需下载**：新版里不同位置的文件内容相同时，只下载一份并正确生成每个目标文件，避免因此回退为约 170 MB 的完整包。
- **下载提示与实际一致**：网络不支持分段下载或增量校验失败时，界面改为“完整下载”及完整包大小，并说明增量失败原因；下载完成后重新检查或复用已下载内容也保留这些信息。
- **安装重启自动关闭界面**：后台服务交接安装并退出后，界面随之关闭，替换完成后自动打开新版。

## 功能与须知

战斗统计、浮窗与性能变化见 [0.1.5 更新说明](release-notes-0.1.5.md)。功能用法、数据保存位置及已知问题见[使用说明](user-guide.md)。

- **反馈问题**：在“诊断”页导出排查包，连同问题描述与复现步骤提交到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues)。
- **支持作者**：欢迎在[爱发电](https://afdian.com/a/minamichiwa)或 [Patreon](https://patreon.com/chiwaminami) 赞助作者一杯咖啡。
