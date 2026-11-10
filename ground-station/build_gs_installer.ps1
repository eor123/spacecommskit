# build_gs_installer.ps1 -- build the SCK Ground Station installer.
# Put this file and SCK_Ground_Station.iss in the GS project folder
# (the one with OpenLstGroundStation.csproj), then from PowerShell:
#
#   cd C:\Users\maxor\source\repos\2022_projects\OpenLST_GroundStation
#   powershell -ExecutionPolicy Bypass -File .\build_gs_installer.ps1 -Version 1.3.0

param(
    [string]$Version  = "1.3.0",
    [string]$RepoInst = "C:\Users\maxor\source\repos\spacecommskit\installer"   # repo installer folder (rtlsdr\ lives here; exe is copied here)
)
$ErrorActionPreference = "Stop"
$Proj    = $PSScriptRoot
$Publish = Join-Path $Proj "bin\Release\net8.0-windows\publish"
$Iss     = Join-Path $Proj "SCK_Ground_Station.iss"

function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Fail($m) { Write-Host "STOP: $m" -ForegroundColor Red; exit 1 }

Step "0. Checks"
if (-not (Test-Path (Join-Path $Proj "OpenLstGroundStation.csproj"))) { Fail "run this from the GS project folder" }
if (-not (Test-Path $Iss)) { Fail "SCK_Ground_Station.iss not found next to this script" }
$RtlSdr = Join-Path $RepoInst "rtlsdr"
if (-not (Test-Path (Join-Path $RtlSdr "rtl_power.exe"))) { Fail "rtl_power.exe not found in $RtlSdr" }
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Fail "Inno Setup 6 not found -- install from https://jrsoftware.org/isinfo.php" }
$flightTag = Select-String -Path (Join-Path $Proj "MainForm.cs") -Pattern 'SCK Ground Station v([0-9.]+)' | Select-Object -First 1
if ($flightTag -and $flightTag.Matches[0].Groups[1].Value -ne $Version) {
    Write-Host "  WARNING: MainForm.cs flight-log tag says v$($flightTag.Matches[0].Groups[1].Value), installer is v$Version" -ForegroundColor Yellow
    Write-Host "           (line with `"ground_station`": `"SCK Ground Station v...`") -- update it and re-run" -ForegroundColor Yellow
}

Step "1. Clean publish folder"
if (Test-Path $Publish) { Remove-Item $Publish -Recurse -Force }

Step "2. dotnet publish (Release, win-x64, self-contained)"
dotnet publish (Join-Path $Proj "OpenLstGroundStation.csproj") -c Release -r win-x64 --self-contained true -o $Publish
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed" }

Step "3. Make sure no settings or keys are in the publish folder"
$bad = Get-ChildItem $Publish -Recurse -Include appsettings.json, customcommands.json, security.h, *.sckkeys -ErrorAction SilentlyContinue
if ($bad) { $bad.FullName | Out-Host; Fail "settings/key files in the publish folder -- remove them and re-run" }
Write-Host "  OK" -ForegroundColor Green

Step "4. Compile installer with Inno Setup"
& $iscc "/DAppVersion=$Version" "/DRtlSdrDir=$RtlSdr" $Iss
if ($LASTEXITCODE -ne 0) { Fail "ISCC failed" }

$exe = Join-Path $Proj "installer\Output\SCK_Ground_Station_Setup_v$Version.exe"
if (-not (Test-Path $exe)) { Fail "installer not found at $exe" }
$mb  = [math]::Round((Get-Item $exe).Length / 1MB, 1)
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
Copy-Item $exe $RepoInst -Force
Step "Done"
Write-Host "  $exe"
Write-Host "  $mb MB   SHA256 $sha"
Write-Host "  copied to $RepoInst  (ready for the firmware-repo commit)"
