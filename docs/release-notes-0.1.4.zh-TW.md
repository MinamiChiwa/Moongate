# Moongate 0.1.4 更新說明

[简体中文](release-notes-0.1.4.md) · [繁體中文](release-notes-0.1.4.zh-TW.md) · [使用說明](user-guide.zh-TW.md) · [回到首頁](../README.zh-TW.md)

<!-- release-header:start -->
> **測試版提示**
>
> Moongate 仍屬於測試版：各項功能已經可以使用，但可能還有問題。遇到問題或有建議，歡迎到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues) 回報。

**這一版修了什麼：** 更新狀態顯示不準的問題——以前不手動點「檢查更新」就一直顯示「已是最新版本」，更新完重新啟動後還顯示「有新版本」。現在每次開啟約 1 分鐘後自動檢查，狀態如實顯示。

**下載哪個檔案：** 在 [GitHub Releases](https://github.com/MinamiChiwa/Moongate/releases) 的 v0.1.4 發行下方 Assets 裡，下載排在最上面的 **`Moongate-v0.1.4-Windows-x64.zip`**（約 170 MB，解壓縮後約 430 MB）。完整解壓縮到一個一般資料夾（例如 `D:\Moongate`），按兩下裡面的 **`Moongate.exe`** 即可執行。

**從舊版本升級：** 在設定頁「更新」裡「檢查更新」後「安裝並重新啟動」。0.1.3 是增量更新（約 24 MB），更早的版本是完整下載。也可以手動升級：下載上面的正式包解壓縮，把裡面的檔案覆蓋到原來的 Moongate 資料夾（保留 `userdata`），再執行 `Moongate.exe`。

| 檔案 | 用途 |
|---|---|
| `Moongate-v0.1.4-Windows-x64.zip` | 正式包，下載這一個即可 |
| `Moongate-v0.1.4-Windows-x64.zip.manifest.json` | 應用程式內更新使用的檔案清單，不用下載 |
| `Moongate-v0.1.4-Windows-x64.zip.sha256` | 正式包的 SHA-256 校驗值，用來核對下載是否完整，一般使用者不用下載 |
| `Moongate-v0.1.4-Windows-x64.zip.update-from-0.1.3.zip` | 0.1.3 應用程式內更新使用的增量包，不能單獨解壓縮使用，不用下載 |
| Source code (zip / tar.gz) | GitHub 自動附上的原始碼，一般使用者不用下載 |
<!-- release-header:end -->

## 這一版的變化

- **自動檢查更新**：每次開啟 Moongate 約 1 分鐘後自動檢查一次，之後按設定的間隔檢查。檢查失敗（例如暫時連不上 GitHub）會在 30 分鐘內再試。檢查只查看有沒有新版本，不會自動下載或安裝。
- **更新狀態如實顯示**：更新卡片新增「上次檢查」一列。還沒檢查過時顯示「尚未檢查」，只有檢查成功且確實沒有新版本時才顯示「已是最新版本」；檢查失敗會寫明原因。
- **更新完成後不再殘留「有新版本」**：應用程式內更新或手動覆蓋升級之後，舊的「有新版本」提示和下載按鈕會自動消失。
- **染色、合奏進行中也能看到新版本**：以前這些活動期間檢查會一直停在「正在檢查」。現在會先顯示「發現新版本」，需要下載多少在活動結束後補上。
- **下載到一半關閉後可以續傳**：重新開啟後「下載進度」顯示「已下載 N MB，可續傳」，再次下載會接著下。
- **之後的增量更新不限出發版本**：從這一版起，Moongate 更新時直接從正式包裡取本機缺少的檔案，不管隔了幾個版本，都只下載有變化的部分。這項能力在下一個版本發布時開始生效。
- 更新留下的暫存檔會自動清理，`userdata\update` 不會越積越多。

## 已知問題與限制

- 遊戲開著、工具無人操作時，背景服務在部分電腦上仍佔用約 0.2 個 CPU 核心，後續版本繼續最佳化。
- 加速器下的抓包按遊戲連線識別，沒有在每一款加速器上實測。仍然收不到資料時，請在遊戲裡停留約 30 秒後到「診斷」頁匯出排查包回報。
- 其餘已知問題見 [0.1.3 更新說明](release-notes-0.1.3.zh-TW.md)與 [0.1.0 更新說明](release-notes-0.1.0.zh-TW.md)。

## 功能與須知

Moongate 的全部功能、使用前須知（需要自行安裝 Npcap、帳號風險、資料儲存位置等）見 [0.1.0 更新說明](release-notes-0.1.0.zh-TW.md)與[使用說明](user-guide.zh-TW.md)；加速器下的抓包、啟動失敗的復原、精準尋色的改進見 [0.1.3 更新說明](release-notes-0.1.3.zh-TW.md)。

- **回報問題**：在「診斷」頁匯出排查包（後端沒啟動時匯出啟動記錄），連同問題描述與重現步驟提交到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues)。
- **支持作者**：如果 Moongate 對你有幫助，歡迎在[愛發電](https://afdian.com/a/minamichiwa)或 [Patreon](https://patreon.com/chiwaminami) 贊助作者一杯咖啡。
