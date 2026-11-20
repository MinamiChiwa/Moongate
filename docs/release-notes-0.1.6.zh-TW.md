# Moongate 0.1.6 更新說明

[简体中文](release-notes-0.1.6.md) · [繁體中文](release-notes-0.1.6.zh-TW.md) · [使用說明](user-guide.zh-TW.md) · [回到首頁](../README.zh-TW.md)

<!-- release-header:start -->
> **測試版提示**
>
> Moongate 仍屬於測試版：各項功能已經可以使用，但可能還有問題。遇到問題或有建議，歡迎到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues) 回報。

**這一版的重點：** 修復增量更新意外下載完整包、下載方式與大小顯示不一致，以及安裝重新啟動必須手動關閉介面的問題。

**下載哪個檔案：** 在 [GitHub Releases](https://github.com/MinamiChiwa/Moongate/releases) 的 v0.1.6 發行下方 Assets 裡，下載排在最上面的 **`Moongate-v0.1.6-Windows-x64.zip`**（約 170 MB，解壓縮後約 430 MB）。完整解壓縮到一個一般資料夾（例如 `D:\Moongate`），按兩下裡面的 **`Moongate.exe`** 即可執行。

**從 0.1.5 或更早版本升級：** 本次升級仍使用舊版更新器，可能完整下載並在安裝時需要手動關閉介面。建議下載正式包，關閉 Moongate 後，用新包的 `Moongate.exe`、`components` 與 `licenses` 取代原資料夾裡的同名三項，保留 `userdata`。本版修復會在執行 0.1.6 後的後續更新中生效。

| 檔案 | 用途 |
|---|---|
| `Moongate-v0.1.6-Windows-x64.zip` | 正式包，下載這一個即可 |
| `Moongate-v0.1.6-Windows-x64.zip.manifest.json` | 應用程式內更新使用的檔案清單，不用下載 |
| `Moongate-v0.1.6-Windows-x64.zip.sha256` | 正式包的 SHA-256 校驗值，用來核對下載是否完整，一般使用者不用下載 |
| Source code (zip / tar.gz) | GitHub 自動附上的原始碼，一般使用者不用下載 |
<!-- release-header:end -->

## 這一版的變化

- **增量更新保持按需下載**：新版裡不同位置的檔案內容相同時，只下載一份並正確產生每個目標檔案，避免因此回退為約 170 MB 的完整包。
- **下載提示與實際一致**：網路不支援分段下載或增量校驗失敗時，介面改為「完整下載」及完整包大小，並說明增量失敗原因；下載完成後重新檢查或重用已下載內容也保留這些資訊。
- **安裝重新啟動自動關閉介面**：背景服務交接安裝並退出後，介面隨之關閉，替換完成後自動開啟新版。

## 功能與須知

戰鬥統計、浮窗與效能變化見 [0.1.5 更新說明](release-notes-0.1.5.zh-TW.md)。功能用法、資料儲存位置及已知問題見[使用說明](user-guide.zh-TW.md)。

- **回報問題**：在「診斷」頁匯出排查包，連同問題描述與重現步驟提交到 [GitHub Issues](https://github.com/MinamiChiwa/Moongate/issues)。
- **支持作者**：歡迎在[愛發電](https://afdian.com/a/minamichiwa)或 [Patreon](https://patreon.com/chiwaminami) 贊助作者一杯咖啡。
