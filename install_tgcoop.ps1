# TGCoop 合作模組自動安裝程式 (Tainted Grail: The Fall of Avalon)
# 由 install_TGCoop.bat 呼叫；也可直接用 PowerShell 執行。
param([string]$GameDirArg = "")

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}

$GameExe    = "Fall of Avalon.exe"
$GameFolder = "Tainted Grail FoA"
$AppId      = "1466060"
$Src        = $PSScriptRoot
$LogFile    = Join-Path $Src "install_log.txt"

function Log {
    param([string]$Msg, [string]$Color = "Gray")
    Write-Host $Msg -ForegroundColor $Color
    try { Add-Content -Path $LogFile -Value ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $Msg) -Encoding UTF8 } catch {}
}
function Finish {
    param([int]$Code = 0)
    Write-Host ""
    try { Read-Host "按 Enter 鍵結束" | Out-Null } catch {}
    exit $Code
}
function Test-GameDir {
    param([string]$Dir)
    if ([string]::IsNullOrWhiteSpace($Dir)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $Dir $GameExe))
}
function Find-InLibrary {
    param([string]$Lib)
    if ([string]::IsNullOrWhiteSpace($Lib)) { return $null }
    $g = Join-Path $Lib "steamapps\common\$GameFolder"
    if (Test-GameDir $g) { return $g }
    return $null
}

try { Set-Content -Path $LogFile -Value ("=== TGCoop install {0} ===" -f (Get-Date)) -Encoding UTF8 } catch {}
Log ("Windows: " + [Environment]::OSVersion.VersionString + "   PowerShell: " + $PSVersionTable.PSVersion)
Log ("Source: " + $Src)

Write-Host "=============================================="
Write-Host "   TGCoop 合作模組 自動安裝程式"
Write-Host "   Tainted Grail: The Fall of Avalon"
Write-Host "=============================================="
Write-Host ""

# ---------- 1. 檢查模組檔案 ----------
$need = @("winhttp.dll", "doorstop_config.ini", "BepInEx\plugins\TGCoop.dll", "BepInEx\core\BepInEx.Preloader.dll")
$missing = $need | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Src $_)) }
if ($missing) {
    Log "[X] 找不到模組檔案：$($missing -join ', ')" Red
    Log "    請先把整個 ZIP「解壓縮」到一個資料夾，再從那個資料夾裡執行 install_TGCoop.bat。" Yellow
    Log "    不要直接在壓縮檔視窗裡雙擊。" Yellow
    Finish 1
}

# ---------- 2. 檢查遊戲是否執行中 ----------
if (Get-Process -Name "Fall of Avalon" -ErrorAction SilentlyContinue) {
    Log "[X] 遊戲正在執行中，請先完全關閉遊戲，再重新執行本程式。" Red
    Finish 1
}

# ---------- 3. 尋找遊戲資料夾 ----------
$GameDir = $null
if (Test-GameDir $GameDirArg) { $GameDir = $GameDirArg.TrimEnd("\") }

if (-not $GameDir) {
    Log "[*] 正在尋找遊戲安裝位置..." Cyan
    $steamPaths = @()
    foreach ($k in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
        try {
            $p = Get-ItemProperty -Path $k -ErrorAction Stop
            foreach ($n in @("SteamPath", "InstallPath")) { if ($p.$n) { $steamPaths += ($p.$n -replace "/", "\") } }
        } catch {}
    }
    $steamPaths += "C:\Program Files (x86)\Steam", "C:\Program Files\Steam"
    $steamPaths = $steamPaths | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
    Log ("    Steam: " + ($steamPaths -join " ; "))

    $libs = @()
    foreach ($s in $steamPaths) {
        $libs += $s
        foreach ($vdf in @("steamapps\libraryfolders.vdf", "config\libraryfolders.vdf")) {
            $v = Join-Path $s $vdf
            if (Test-Path -LiteralPath $v) {
                $rx = '"path"\s+"([^"]+)"'
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $v -Raw), $rx)) {
                    $libs += ($m.Groups[1].Value -replace "\\\\", "\")
                }
            }
        }
    }
    foreach ($d in [IO.DriveInfo]::GetDrives()) {
        if ($d.DriveType -ne "Fixed") { continue }
        $r = $d.Name
        $libs += (Join-Path $r "SteamLibrary"), (Join-Path $r "Steam"), (Join-Path $r "Games\Steam"), (Join-Path $r "Program Files (x86)\Steam"), (Join-Path $r "Program Files\Steam")
    }
    foreach ($l in ($libs | Select-Object -Unique)) {
        $g = Find-InLibrary $l
        if ($g) { $GameDir = $g; break }
    }
}

while (-not $GameDir) {
    Write-Host ""
    Log "[!] 找不到遊戲資料夾，請手動指定。" Yellow
    Write-Host "    Steam → 對遊戲按右鍵 → 管理 → 瀏覽本機檔案，"
    Write-Host "    把開啟的資料夾路徑複製貼到這裡（留空直接按 Enter 可結束）："
    $in = Read-Host "遊戲資料夾路徑"
    if ([string]::IsNullOrWhiteSpace($in)) { exit 1 }
    $in = $in.Trim().Trim('"').TrimEnd("\")
    if (Test-GameDir $in) { $GameDir = $in }
    else { Log "[X] 這個資料夾裡沒有 $GameExe，請再確認一次。" Red }
}

Log "[v] 遊戲資料夾：" Green
Log "    $GameDir"
Write-Host ""

# ---------- 4. 權限檢查，必要時以管理員身分重新執行 ----------
$probe = Join-Path $GameDir ".tgcoop_writetest"
try {
    [IO.File]::WriteAllText($probe, "x")
    Remove-Item -LiteralPath $probe -Force
} catch {
    Log "[!] 這個位置需要管理員權限，將彈出 UAC 視窗，請按「是」。" Yellow
    $me = $MyInvocation.MyCommand.Path
    try {
        Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$me`"", "`"$GameDir`"")
        exit 0
    } catch {
        Log "[X] 無法取得管理員權限：$($_.Exception.Message)" Red
        Log "    請對 install_TGCoop.bat 按右鍵，選「以系統管理員身分執行」。" Yellow
        Finish 1
    }
}

# ---------- 5. 複製檔案 ----------
Log "[*] 正在複製檔案..." Cyan
try {
    foreach ($f in @("winhttp.dll", "doorstop_config.ini", ".doorstop_version")) {
        $sf = Join-Path $Src $f
        if (Test-Path -LiteralPath $sf) { Copy-Item -LiteralPath $sf -Destination $GameDir -Force }
    }
    Copy-Item -LiteralPath (Join-Path $Src "BepInEx") -Destination $GameDir -Recurse -Force
} catch {
    Log "[X] 複製檔案失敗：$($_.Exception.Message)" Red
    Log "    請確認遊戲已關閉，或對 install_TGCoop.bat 按右鍵「以系統管理員身分執行」再試一次。" Yellow
    Finish 1
}

# ---------- 6. 驗證 ----------
$ok = $true
foreach ($f in @("winhttp.dll", "doorstop_config.ini", "BepInEx\core\BepInEx.Preloader.dll", "BepInEx\core\BepInEx.dll", "BepInEx\plugins\TGCoop.dll")) {
    if (Test-Path -LiteralPath (Join-Path $GameDir $f)) { Log "    [v] $f" Green }
    else { Log "    [X] 缺少 $f" Red; $ok = $false }
}
if (-not $ok) { Log "[X] 安裝不完整，請把 install_log.txt 傳給主機。" Red; Finish 1 }

Write-Host ""
Write-Host "==============================================" -ForegroundColor Green
Write-Host "   [v] 安裝完成！" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green
Write-Host ""
Log "接下來需要啟動遊戲一次，讓模組產生設定檔並確認有載入成功。"
Write-Host ""

$ans = Read-Host "要現在透過 Steam 啟動遊戲嗎？(Y/N)"
$logPath = Join-Path $GameDir "BepInEx\LogOutput.log"

if ($ans -match '^[Yy]') {
    Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
    Start-Process "steam://rungameid/$AppId"
    Log "[*] 遊戲啟動中，正在等待模組載入（最多等 2 分鐘）..." Cyan
    $loaded = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path -LiteralPath $logPath) -and (Select-String -LiteralPath $logPath -Pattern "Loading \[TGCoop" -Quiet -ErrorAction SilentlyContinue)) { $loaded = $true; break }
    }
    if ($loaded) {
        Write-Host ""
        Log "[v] 模組已成功載入！遊戲可以直接繼續玩。" Green
        Write-Host ""
        Write-Host "合作遊玩方式："
        Write-Host "  - 兩人都先載入各自的存檔"
        Write-Host "  - 主機按 F7 開 Steam 邀請視窗，對方從 Steam 接受邀請"
        Write-Host "  - F4 傳送到隊友旁、F10 離開連線、H 長按 5 秒扶起倒地隊友"
    } else {
        Write-Host ""
        Log "[!] 等了 2 分鐘還沒看到模組載入的紀錄。" Yellow
        Write-Host "    遊戲進入主選單後請關掉，再用記事本打開："
        Write-Host "    $logPath"
        Write-Host "    找找看有沒有 ""Loading [TGCoop"" 這一行。沒有的話把這個檔案傳給主機。"
    }
} else {
    Write-Host ""
    Write-Host "請自行啟動遊戲一次，到主選單後關掉，再確認這個檔案裡有 ""Loading [TGCoop"" 這一行："
    Write-Host "    $logPath"
}
Finish 0
