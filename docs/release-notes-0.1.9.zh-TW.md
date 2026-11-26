# Moongate 0.1.9 更新說明

[简体中文](release-notes-0.1.9.md) · [繁體中文](release-notes-0.1.9.zh-TW.md) · [使用說明](user-guide.zh-TW.md) · [回到首頁](../README.zh-TW.md)

<!-- release-header:start -->
> **測試版**：各項功能可以使用，但仍可能存在問題。問題與建議請提交至 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues)。

**下載**：Assets 中的 **`Moongate-v0.1.9-Windows-x64.zip`**（約 175 MB），完整解壓縮後執行 `Moongate.exe`。其餘檔案供應用程式內更新與校驗使用，無需下載。

**升級**：0.1.7、0.1.8 可在設定頁「更新」中檢查並安裝，僅下載有變化的檔案。0.1.6 及更早版本請下載正式包，關閉 Moongate 後以新包中的 `Moongate.exe`、`components`、`licenses` 取代原資料夾中的同名項目，保留 `userdata`。
<!-- release-header:end -->

## 工具資料夾不再持續變大

- 染色尋色的執行紀錄（每次尋色的截圖與核驗紀錄，每次約 8–67 MB）只保留最近 3 次。開啟 Moongate 時與每次開始尋色時自動刪除更早的紀錄。
- 應用程式內更新前的資料庫備份只保留最近一次。更新完成後，下次開啟 Moongate 時自動刪除更早各次更新留下的備份；自動備份與手動備份不受影響。
- 背景的自動清理遇到例外時照常繼續，不再因此停止。

## 已知問題

- 0.1.8 的已知問題仍然適用，見 [0.1.8 更新說明](release-notes-0.1.8.zh-TW.md)。

回報問題時，請在「診斷」頁匯出排查包，連同問題描述提交至 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues)。贊助管道：[愛發電](https://afdian.com/a/minamichiwa)、[Patreon](https://patreon.com/chiwaminami)。
