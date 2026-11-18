# Moongate 0.1.4 更新说明

[简体中文](release-notes-0.1.4.md) · [繁體中文](release-notes-0.1.4.zh-TW.md) · [使用说明](user-guide.md) · [返回首页](../README.md)

<!-- release-header:start -->
> **测试版提示**
>
> Moongate 仍属于测试版：各项功能已经可以使用，但可能还有问题。遇到问题或有建议，欢迎到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues) 反馈。

**这一版修了什么：** 更新状态显示不准的问题——以前不手动点“检查更新”就一直显示“已是最新版本”，更新完重启后还显示“有新版本”。现在每次打开约 1 分钟后自动检查，状态如实显示。

**下载哪个文件：** 在 [GitHub Releases](https://github.com/MinamiChiwa/Moongate/releases) 的 v0.1.4 发行下方 Assets 里，下载排在最上面的 **`Moongate-v0.1.4-Windows-x64.zip`**（约 170 MB，解压后约 430 MB）。完整解压到一个普通文件夹（例如 `D:\Moongate`），双击里面的 **`Moongate.exe`** 即可运行。

**从旧版本升级：** 在设置页“更新”里“检查更新”后“安装并重启”。0.1.3 是增量更新（约 24 MB），更早的版本是完整下载。也可以手动升级：下载上面的正式包解压，把里面的文件覆盖到原来的 Moongate 文件夹（保留 `userdata`），再运行 `Moongate.exe`。

| 文件 | 用途 |
|---|---|
| `Moongate-v0.1.4-Windows-x64.zip` | 正式包，下载这一个即可 |
| `Moongate-v0.1.4-Windows-x64.zip.manifest.json` | 应用内更新使用的文件清单，不用下载 |
| `Moongate-v0.1.4-Windows-x64.zip.sha256` | 正式包的 SHA-256 校验值，用来核对下载是否完整，普通用户不用下载 |
| `Moongate-v0.1.4-Windows-x64.zip.update-from-0.1.3.zip` | 0.1.3 应用内更新使用的增量包，不能单独解压使用，不用下载 |
| Source code (zip / tar.gz) | GitHub 自动附带的源代码，普通用户不用下载 |
<!-- release-header:end -->

## 这一版的变化

- **自动检查更新**：每次打开 Moongate 约 1 分钟后自动检查一次，之后按设置的间隔检查。检查失败（例如暂时连不上 GitHub）会在 30 分钟内再试。检查只查看有没有新版本，不会自动下载或安装。
- **更新状态如实显示**：更新卡片新增“上次检查”一行。还没检查过时显示“尚未检查”，只有检查成功且确实没有新版本时才显示“已是最新版本”；检查失败会写明原因。
- **更新完成后不再残留“有新版本”**：应用内更新或手动覆盖升级之后，旧的“有新版本”提示和下载按钮会自动消失。
- **染色、合奏进行中也能看到新版本**：以前这些活动期间检查会一直停在“正在检查”。现在会先显示“发现新版本”，需要下载多少在活动结束后补上。
- **下载到一半关闭后可以续传**：重新打开后“下载进度”显示“已下载 N MB，可续传”，再次下载会接着下。
- **以后的增量更新不限出发版本**：从这一版起，Moongate 更新时直接从正式包里取本机缺少的文件，不管隔了几个版本，都只下载有变化的部分。这项能力在下一个版本发布时开始生效。
- 更新留下的临时文件会自动清理，`userdata\update` 不会越积越多。

## 已知问题与限制

- 游戏开着、工具无人操作时，后台服务在部分电脑上仍占用约 0.2 个 CPU 核心，后续版本继续优化。
- 加速器下的抓包按游戏连接识别，没有在每一款加速器上实测。仍然收不到数据时，请在游戏里停留约 30 秒后到“诊断”页导出排查包反馈。
- 其余已知问题见 [0.1.3 更新说明](release-notes-0.1.3.md)与 [0.1.0 更新说明](release-notes-0.1.0.md)。

## 功能与须知

Moongate 的全部功能、使用前须知（需要自行安装 Npcap、账号风险、数据保存位置等）见 [0.1.0 更新说明](release-notes-0.1.0.md)与[使用说明](user-guide.md)；加速器下的抓包、启动失败的恢复、精准寻色的改进见 [0.1.3 更新说明](release-notes-0.1.3.md)。

- **反馈问题**：在“诊断”页导出排查包（后端没启动时导出启动记录），连同问题描述与复现步骤提交到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues)。
- **支持作者**：如果 Moongate 对你有帮助，欢迎在[爱发电](https://afdian.com/a/minamichiwa)或 [Patreon](https://patreon.com/chiwaminami) 赞助作者一杯咖啡。
