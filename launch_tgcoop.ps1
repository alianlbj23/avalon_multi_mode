# TGCoop 啟動器：開遊戲前先到 GitHub 檢查模組有沒有更新，有就自動更新，然後啟動遊戲。
# 由 play_TGCoop.bat 呼叫。主機和客戶端都用這個開遊戲，模組版本就會一致。
param([switch]$NoLaunch, [switch]$ForceUpdate)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072 } catch {}

$Repo       = "alianlbj23/avalon_multi_mode"
$Branch     = "main"
$GameExe    = "Fall of Avalon.exe"
$GameFolder = "Tainted Grail FoA"
$AppId      = "1466060"
$Kit        = $PSScriptRoot
$LogFile    = Join-Path $Kit "launcher_log.txt"
$PathCache  = Join-Path $Kit "game_path.txt"

function Log {
    param([string]$Msg, [string]$Color = "Gray")
    Write-Host $Msg -ForegroundColor $Color
    try { Add-Content -Path $LogFile -Value ("[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Msg) -Encoding UTF8 } catch {}
}
function Pause-Exit { param([int]$Code = 0); Write-Host ""; try { Read-Host "按 Enter 鍵結束" | Out-Null } catch {}; exit $Code }
function Test-GameDir { param([string]$Dir); if ([string]::IsNullOrWhiteSpace($Dir)) { return $false }; return (Test-Path -LiteralPath (Join-Path $Dir $GameExe)) }

Write-Host "=============================================="
Write-Host "   TGCoop 啟動器：檢查更新 → 啟動遊戲"
Write-Host "=============================================="
Write-Host ""

# ---------- 1. 遊戲資料夾（先用上次記住的，否則自動找） ----------
$GameDir = $null
if (Test-Path -LiteralPath $PathCache) {
    $cached = (Get-Content -LiteralPath $PathCache -Raw).Trim()
    if (Test-GameDir $cached) { $GameDir = $cached }
}
if (-not $GameDir) {
    Log "[*] 正在尋找遊戲安裝位置..." Cyan
    $steamPaths = @()
    foreach ($k in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
        try { $p = Get-ItemProperty -Path $k -ErrorAction Stop; foreach ($n in @("SteamPath", "InstallPath")) { if ($p.$n) { $steamPaths += ($p.$n -replace "/", "\") } } } catch {}
    }
    $steamPaths += "C:\Program Files (x86)\Steam", "C:\Program Files\Steam"
    $libs = @()
    foreach ($s in ($steamPaths | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique)) {
        $libs += $s
        foreach ($vdf in @("steamapps\libraryfolders.vdf", "config\libraryfolders.vdf")) {
            $v = Join-Path $s $vdf
            if (Test-Path -LiteralPath $v) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $v -Raw), '"path"\s+"([^"]+)"')) { $libs += ($m.Groups[1].Value -replace "\\\\", "\") }
            }
        }
    }
    foreach ($d in [IO.DriveInfo]::GetDrives()) {
        if ($d.DriveType -ne "Fixed") { continue }
        $libs += (Join-Path $d.Name "SteamLibrary"), (Join-Path $d.Name "Steam"), (Join-Path $d.Name "Games\Steam"), (Join-Path $d.Name "Program Files (x86)\Steam")
    }
    foreach ($l in ($libs | Select-Object -Unique)) {
        $g = Join-Path $l "steamapps\common\$GameFolder"
        if (Test-GameDir $g) { $GameDir = $g; break }
    }
}
while (-not $GameDir) {
    Log "[!] 找不到遊戲資料夾。Steam → 對遊戲按右鍵 → 管理 → 瀏覽本機檔案，把路徑貼到這裡（留空結束）：" Yellow
    $in = Read-Host "遊戲資料夾路徑"
    if ([string]::IsNullOrWhiteSpace($in)) { exit 1 }
    $in = $in.Trim().Trim('"').TrimEnd("\")
    if (Test-GameDir $in) { $GameDir = $in } else { Log "[X] 這個資料夾裡沒有 $GameExe。" Red }
}
try { Set-Content -LiteralPath $PathCache -Value $GameDir -Encoding UTF8 } catch {}
Log "[v] 遊戲資料夾：$GameDir" Green

# ---------- 2. 遊戲不能在執行中 ----------
if (Get-Process -Name "Fall of Avalon" -ErrorAction SilentlyContinue) {
    Log "[X] 遊戲已經在執行中。請先關閉遊戲，再用這個啟動器開。" Red
    Pause-Exit 1
}

# ---------- 3. 查 GitHub 最新版本 ----------
$VersionFile = Join-Path $GameDir "BepInEx\tgcoop_version.txt"
$localSha = ""
if (Test-Path -LiteralPath $VersionFile) { $localSha = (Get-Content -LiteralPath $VersionFile -Raw).Trim() }
$remoteSha = $null
try {
    $hdr = @{ "User-Agent" = "TGCoop-launcher"; "Accept" = "application/vnd.github+json" }
    $info = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/commits/$Branch" -Headers $hdr -TimeoutSec 15
    $remoteSha = [string]$info.sha
    $when = ""; try { $when = ([datetime]$info.commit.committer.date).ToLocalTime().ToString("yyyy-MM-dd HH:mm") } catch {}
    Log ("[*] GitHub 最新版本：" + $remoteSha.Substring(0, 7) + "  ($when)  " + [string]$info.commit.message.Split("`n")[0])
} catch {
    Log "[!] 無法連到 GitHub 檢查更新（$($_.Exception.Message)）。先用目前版本啟動。" Yellow
}

$installed = (Test-Path -LiteralPath (Join-Path $GameDir "BepInEx\plugins\TGCoop.dll"))
$needUpdate = $false
if ($remoteSha) {
    if ($ForceUpdate -or -not $installed -or $localSha -ne $remoteSha) { $needUpdate = $true }
}
if (-not $needUpdate) {
    if ($remoteSha) { Log ("[v] 模組已是最新（" + $localSha.Substring(0, [Math]::Min(7, $localSha.Length)) + "）。") Green }
}

# ---------- 4. 下載並更新 ----------
if ($needUpdate) {
    Log ("[*] 更新模組：本機 " + $(if ($localSha) { $localSha.Substring(0,7) } else { "（未安裝）" }) + " → " + $remoteSha.Substring(0,7)) Cyan
    $tmp = Join-Path $env:TEMP ("tgcoop_update_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $zip = Join-Path $tmp "repo.zip"
    try {
        Invoke-WebRequest -Uri "https://github.com/$Repo/archive/refs/heads/$Branch.zip" -OutFile $zip -UseBasicParsing -TimeoutSec 120
        Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
        $src = Get-ChildItem -LiteralPath $tmp -Directory | Where-Object { Test-Path (Join-Path $_.FullName "BepInEx\plugins\TGCoop.dll") } | Select-Object -First 1
        if (-not $src) { throw "下載的壓縮檔裡找不到 BepInEx\plugins\TGCoop.dll" }
        $src = $src.FullName

        # 權限檢查
        $probe = Join-Path $GameDir ".tgcoop_writetest"
        try { [IO.File]::WriteAllText($probe, "x"); Remove-Item -LiteralPath $probe -Force } catch {
            Log "[!] 遊戲資料夾需要管理員權限，將彈出 UAC，請按「是」。" Yellow
            $argsList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$($MyInvocation.MyCommand.Path)`"")
            if ($NoLaunch) { $argsList += "-NoLaunch" }
            Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $argsList
            exit 0
        }

        foreach ($f in @("winhttp.dll", "doorstop_config.ini", ".doorstop_version")) {
            $sf = Join-Path $src $f
            if (Test-Path -LiteralPath $sf) { Copy-Item -LiteralPath $sf -Destination $GameDir -Force }
        }
        Copy-Item -LiteralPath (Join-Path $src "BepInEx\core") -Destination (Join-Path $GameDir "BepInEx") -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $src "BepInEx\plugins") -Destination (Join-Path $GameDir "BepInEx") -Recurse -Force
        Set-Content -LiteralPath $VersionFile -Value $remoteSha -Encoding ASCII

        # 也更新啟動器 / 安裝程式自己（這個資料夾），下次就是新版腳本
        foreach ($f in @("install_TGCoop.bat", "install_tgcoop.ps1", "play_TGCoop.bat", "launch_tgcoop.ps1", "README.md", "TGCoopPlus 說明.txt")) {
            $sf = Join-Path $src $f
            if (Test-Path -LiteralPath $sf) { try { Copy-Item -LiteralPath $sf -Destination $Kit -Force } catch {} }
        }
        Log ("[v] 更新完成 → " + $remoteSha.Substring(0,7)) Green
    } catch {
        Log "[X] 更新失敗：$($_.Exception.Message)" Red
        Log "    先用目前已安裝的版本啟動；若兩邊版本不同，請手動重新下載安裝。" Yellow
        if (-not $installed) { Pause-Exit 1 }
    } finally {
        try { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    }
}

# ---------- 5. 桌面捷徑（只建立一次） ----------
try {
    $desktop = [Environment]::GetFolderPath("Desktop")
    $lnk = Join-Path $desktop "TGCoop 啟動遊戲.lnk"
    $bat = Join-Path $Kit "play_TGCoop.bat"
    if (-not (Test-Path -LiteralPath $lnk) -and (Test-Path -LiteralPath $bat)) {
        $ws = New-Object -ComObject WScript.Shell
        $s = $ws.CreateShortcut($lnk)
        $s.TargetPath = $bat
        $s.WorkingDirectory = $Kit
        $s.IconLocation = (Join-Path $GameDir $GameExe) + ",0"
        $s.Save()
        Log "[v] 已在桌面建立捷徑「TGCoop 啟動遊戲」。" Green
    }
} catch {}

# ---------- 6. 啟動遊戲 ----------
if ($NoLaunch) { Log "[*] -NoLaunch：不啟動遊戲。"; exit 0 }
Log "[*] 啟動遊戲..." Cyan
Start-Process "steam://rungameid/$AppId"
Start-Sleep -Seconds 3
exit 0
