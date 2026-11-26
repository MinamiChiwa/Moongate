# Moongate 0.1.9 更新说明

[简体中文](release-notes-0.1.9.md) · [繁體中文](release-notes-0.1.9.zh-TW.md) · [使用说明](user-guide.md) · [返回首页](../README.md)

<!-- release-header:start -->
> **测试版**：各项功能可以使用，但仍可能存在问题。问题与建议请提交至 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues)。

**下载**：Assets 中的 **`Moongate-v0.1.9-Windows-x64.zip`**（约 175 MB），完整解压后运行 `Moongate.exe`。其余文件供应用内更新与校验使用，无需下载。

**升级**：0.1.7、0.1.8 可在设置页“更新”中检查并安装，仅下载有变化的文件。0.1.6 及更早版本请下载正式包，关闭 Moongate 后以新包中的 `Moongate.exe`、`components`、`licenses` 替换原文件夹中的同名项目，保留 `userdata`。
<!-- release-header:end -->

## 工具文件夹不再持续变大

- 染色寻色的运行记录（每次寻色的截图与核验记录，每次约 8–67 MB）只保留最近 3 次。打开 Moongate 时与每次开始寻色时自动删除更早的记录。
- 应用内更新前的数据库备份只保留最近一次。更新完成后，下次打开 Moongate 时自动删除更早各次更新留下的备份；自动备份与手动备份不受影响。
- 后台的自动清理遇到异常时照常继续，不再因此停止。

## 已知问题

- 0.1.8 的已知问题仍然适用，见 [0.1.8 更新说明](release-notes-0.1.8.md)。

反馈问题时，请在“诊断”页导出排查包，连同问题描述提交至 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues)。赞助渠道：[爱发电](https://afdian.com/a/minamichiwa)、[Patreon](https://patreon.com/chiwaminami)。
