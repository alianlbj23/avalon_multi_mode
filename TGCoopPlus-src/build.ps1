param([string]$OutDir = $PSScriptRoot)
$ErrorActionPreference = "Stop"
$game = "D:\SteamLibrary\steamapps\common\Tainted Grail FoA"
$managed = "$game\Fall of Avalon_Data\Managed"
$core = "$game\BepInEx\core"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$refs = @(
    "$managed\mscorlib.dll", "$managed\System.dll", "$managed\System.Core.dll", "$managed\netstandard.dll",
    "$managed\UnityEngine.dll", "$managed\UnityEngine.CoreModule.dll", "$managed\UnityEngine.IMGUIModule.dll",
    "$managed\UnityEngine.InputLegacyModule.dll", "$managed\UnityEngine.TextRenderingModule.dll",
    "$managed\TG.Main.dll", "$managed\Awaken.Utility.dll", "$managed\com.rlabrecque.steamworks.net.dll", "$managed\Animancer.dll",
    "$managed\UnityEngine.AnimationModule.dll", "$managed\Awaken.PackageUtilities.dll",
    "$core\BepInEx.dll", "$core\0Harmony.dll"
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "missing reference: $r" } }

$out = Join-Path $OutDir "TGCoopPlus.dll"
$args = @("/nologo", "/nostdlib+", "/noconfig", "/target:library", "/optimize+", "/warn:1", "/out:$out", "/platform:anycpu")
foreach ($r in $refs) { $args += "/reference:$r" }
$args += (Join-Path $PSScriptRoot "TGCoopPlus.cs")

& $csc @args
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
"built: $out  ($((Get-Item $out).Length) bytes)"
