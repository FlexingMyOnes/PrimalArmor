# 公開發布與更新

專案：<https://github.com/FlexingMyOnes/PrimalArmor>

自訂插件庫：<https://raw.githubusercontent.com/FlexingMyOnes/PrimalArmor/main/repo.json>

第一版為 `v0.1.0-alpha.1` 公開測試版，目標是台服 7.25 / Dalamud API 13。已完成編譯及離線檢查，尚待實機與多人測試。

後續發布步驟：

1. 依 `TESTING.md` 完成實機測試，找不同模組位置／集合配置的玩家測試。
2. 更新 DLL 與 `PrimalArmor.json` 的相同版本號，重新建置及執行離線檢查。
3. 提交原始碼，排除 bin、obj、本機日誌、個人設定與原作者模組資源。
4. 建立新的 GitHub Release，預覽版勾選 pre-release，並上傳版本 ZIP 及原始碼包。
5. 產生並提交新的 `repo.json`：

```powershell
.\tools\New-RepositoryManifest.ps1 -DownloadUrl 'https://github.com/FlexingMyOnes/PrimalArmor/releases/download/v0.1.0-alpha.1/PrimalArmor-0.1.0.0.zip' -RepositoryUrl 'https://github.com/FlexingMyOnes/PrimalArmor' -OutputPath '.\repo.json'
```

更新時請替換為新版本的真實下載網址。使用者保留同一個自訂插件庫網址，即可從安裝器取得新版本。不能只替換 ZIP 而維持相同版本號。此第一版雖標記 GitHub pre-release，加入自訂插件庫後即可安裝，無須另開 Dalamud 測試插件選項。

完整格式參考 [Dalamud 自訂插件庫文件](https://dalamud.dev/plugin-publishing/custom-repositories/)。本專案透過自訂插件庫發布，不代表官方插件庫已審查或核准。
