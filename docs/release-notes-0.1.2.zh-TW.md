# Moongate 0.1.2 更新說明

[简体中文](release-notes-0.1.2.md) · [繁體中文](release-notes-0.1.2.zh-TW.md) · [使用說明](user-guide.zh-TW.md) · [回到首頁](../README.zh-TW.md)

<!-- release-header:start -->
> **測試版提示**
>
> Moongate 仍屬於測試版：各項功能已經可以使用，但可能還有問題。遇到問題或有建議，歡迎到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues) 回報。

**這一版改了什麼：** 應用程式內更新改為直接連線 GitHub 檢查和下載，不再因為介面限流或網路原因檢查失敗；從這一版起，之後的更新只下載有變化的檔案（增量更新），通常只有二十多 MB，不用每次下載完整包。

**下載哪個檔案：** 在 [GitHub Releases](https://github.com/MinamiChiwa/Moongate/releases) 的 v0.1.2 發行下方 Assets 裡，下載排在最上面的 **`Moongate-v0.1.2-Windows-x64.zip`**（約 170 MB，解壓縮後約 430 MB）。完整解壓縮到一個一般資料夾（例如 `D:\Moongate`），按兩下裡面的 **`Moongate.exe`** 即可執行。

**從 0.1.0 / 0.1.1 升級：** 在設定頁「更新」裡點「檢查更新」，「下載更新」後點「安裝並重新啟動」，這一次仍是完整下載。如果檢查更新失敗，請手動升級：下載上面的正式包解壓縮到一個新資料夾，關閉 Moongate，把舊資料夾裡的 `userdata` 整個移到新資料夾，再執行新的 `Moongate.exe`。升到 0.1.2 之後，之後的更新就是增量更新。

| 檔案 | 用途 |
|---|---|
| `Moongate-v0.1.2-Windows-x64.zip` | 正式包，下載這一個即可 |
| `Moongate-v0.1.2-Windows-x64.zip.manifest.json` | 應用程式內更新使用的檔案清單，不用下載 |
| `Moongate-v0.1.2-Windows-x64.zip.sha256` | 正式包的 SHA-256 校驗值，用來核對下載是否完整，一般使用者不用下載 |
| `Moongate-v0.1.2-Windows-x64.zip.update-from-0.1.1.zip` | 應用程式內更新使用的增量包，不能單獨解壓縮使用，不用下載 |
| Source code (zip / tar.gz) | GitHub 自動附上的原始碼，一般使用者不用下載 |
<!-- release-header:end -->

## 這一版的變化

- **檢查更新直接連線 GitHub**：從 GitHub 的發行頁面取得最新版本並下載，不再依賴容易被限流的介面。連不上時顯示「連不上 GitHub」，沒有新版本時顯示「已是最新版本」，兩種情況分開提示。
- **增量更新**：檢查更新時，Moongate 把本機的程式檔案和新版本逐個比對，內容相同的直接沿用，只下載有變化的檔案。設定頁會寫明本次是「增量更新」還是「完整下載」以及下載大小。下載支援續傳；新版本拼好後逐個檔案核對，全部一致才會安裝。
- **自動改用完整下載的情況**：跨了多個版本、本機程式檔案被改動過，或增量包下載、核對失敗時，自動改為完整下載，不需要手動處理。
- **安裝失敗自動還原、資料保留**：與之前相同，新版本啟動失敗時回到原版本，`userdata` 裡的資料保留。

## 已知問題與限制

- 遊戲開著、工具無人操作時，背景服務在部分電腦上仍佔用約 0.2 個 CPU 核心，後續版本繼續最佳化。
- 遊戲視窗正在被拖曳或調整大小時，在設定頁手動點「開始檢測」可能提示綁定已變化，等視窗停穩後再點一次即可。
- 其餘已知問題見 [0.1.0 更新說明](release-notes-0.1.0.zh-TW.md)。

## 功能與須知

Moongate 的全部功能、使用前須知（需要自行安裝 Npcap、帳號風險、資料儲存位置等）見 [0.1.0 更新說明](release-notes-0.1.0.zh-TW.md)與[使用說明](user-guide.zh-TW.md)；對 11 月 18 日更新後遊戲的支援見 [0.1.1 更新說明](release-notes-0.1.1.zh-TW.md)。

- **回報問題**：在「診斷」頁匯出排查包，連同問題描述與重現步驟提交到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues)。
- **支持作者**：如果 Moongate 對你有幫助，歡迎在[愛發電](https://afdian.com/a/minamichiwa)或 [Patreon](https://patreon.com/chiwaminami) 贊助作者一杯咖啡。
