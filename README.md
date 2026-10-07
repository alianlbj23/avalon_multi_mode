# avalon_multi_mode

《Tainted Grail: The Fall of Avalon》雙人合作模組安裝包：**TGCoop 0.5.36** + 自製補丁 **TGCoopPlus**。

## 安裝（兩個人都要裝）

1. 下載整個 repo（Code → Download ZIP）並解壓縮。
2. 關閉遊戲，雙擊 `install_TGCoop.bat`。它會自動找到 Steam 遊戲庫、複製檔案，必要時要求管理員權限。
3. 問要不要啟動遊戲時按 `Y`，看到「模組已成功載入」就完成。

手動安裝的話，把 `winhttp.dll`、`doorstop_config.ini`、`BepInEx\` 複製到遊戲資料夾（和 `Fall of Avalon.exe` 同一層）。

## 每次開遊戲：用 `play_TGCoop.bat`

**兩個人都用這個開遊戲**，它會先到 GitHub 檢查這個 repo 有沒有新 commit，有就自動下載並更新模組（含 TGCoopPlus），再透過 Steam 啟動遊戲。這樣主機和客戶端的模組版本永遠一致。第一次執行會在桌面建立「TGCoop 啟動遊戲」捷徑，之後點捷徑就好（不要刪掉解壓縮出來的資料夾）。

- 連不上 GitHub 時會直接用目前版本啟動。
- 強制重裝：`play_TGCoop.bat -ForceUpdate`；只更新不開遊戲：`play_TGCoop.bat -NoLaunch`。
- 更新紀錄在 `launcher_log.txt`。

## 兩人實測

照 [測試流程.md](測試流程.md) 跑一遍（約 15 分鐘），出問題時兩人都按面板的「匯出診斷到桌面」，把資料夾傳回來。

## 遊玩

- 兩人都先**載入存檔**，再連線。
- 按 `Insert` 開連線面板：選「主機」或「客戶端」。主機按一鍵建房並開 Steam 邀請視窗，客戶端從面板的好友清單直接加入。
- 其他按鍵：`F7` 開房、`F3` 傳送到隊友旁、`F10` 離開、`H` 長按扶起倒地隊友、`F8` 幽靈模式（自我測試用）。
- 進度以主機為準。客戶端的世界向主機收斂，主機的任務與經驗不受客戶端影響。

## TGCoopPlus 做了什麼

| 功能 | 說明 |
|---|---|
| LegsFix / MaskFix / AuxLayerFix | 第一人稱沒有腿部狀態機，分身腿部改由速度直接驅動、蹲下換蹲姿片段；上半身層改用不含腿的遮罩（原本用第一人稱全身遮罩把腿凍住）；工具層待機時淡出，不再蓋住手臂動作。 |
| AnimLayerFix | 遊戲更新後動畫圖層編號改變（Legs 9→10），原模組寫死舊值導致隊友 T-pose。啟動時以執行中的遊戲列舉值修正。 |
| StoryRetry | 世界還沒載入時暫存主機送來的任務 / 旗標封包，載好後再套用，不再丟失。 |
| HostAuthority | 切斷客戶端 → 主機的任務、旗標、劇情獎勵同步，避免主機被動完成任務而經驗暴漲。 |
| Panel | `Insert` 開啟的連線面板，可設定主機 / 客戶端、建房、邀請、加入好友房間、要求重新同步。 |
| SharedKillExp | 隊友（或主機確認的遠端擊殺）在你的世界殺死敵人時，你也獲得擊殺經驗。原本遠端擊殺的攻擊者是代理 NPC，遊戲不發經驗。 |
| ErrorWatcher | 畫面上方紅字提示 TGCoop 錯誤數，面板列出最近幾筆，一鍵把 log 與設定匯出到桌面 `TGCoop_diag_時間` 資料夾。 |
| AnimDebug | 記錄套用到隊友分身的遠端動畫狀態，方便除錯。 |

設定檔：`BepInEx\config\com.tgcoop.plus.cfg`。

## 從原始碼編譯

原始碼在 `TGCoopPlus-src/`，只需要 Windows 內建的 C# 編譯器：

```powershell
powershell -ExecutionPolicy Bypass -File TGCoopPlus-src\build.ps1
```

`build.ps1` 裡的遊戲路徑預設是 `D:\SteamLibrary\steamapps\common\Tainted Grail FoA`，不同的話改第一行。

## 版本

- 遊戲 build 25629220
- TGCoop 0.5.36（原作者的閉源模組，`LEEME-TGCoop.txt` 為原始說明）
- TGCoopPlus 1.6.3
- BepInEx 5.4.23.3
