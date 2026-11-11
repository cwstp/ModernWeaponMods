# Builds the mod DLL and deploys it into the live SPT mod folder.
# Requires the .NET 10 SDK (SPT 4.1.x targets net10.0; 4.1.6 tested).
$ErrorActionPreference = "Stop"

$src = "D:\SPT\_ModDev\ModernWeaponMods"
$mod = "D:\SPT\SPT\user\mods\ModernWeaponMods"

Write-Host "Building (Release)..." -ForegroundColor Cyan
dotnet build "$src\ModernWeaponMods.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$dll = Join-Path $src "bin\Release\ModernWeaponMods.dll"
if (-not (Test-Path $dll)) { throw "Build did not produce $dll" }

if (-not (Test-Path $mod)) { New-Item -ItemType Directory -Path $mod | Out-Null }
Copy-Item $dll (Join-Path $mod "ModernWeaponMods.dll") -Force

Write-Host "Deployed DLL -> $mod" -ForegroundColor Green
Write-Host "Mod folder now contains:" -ForegroundColor Cyan
Get-ChildItem -Recurse $mod | Select-Object -ExpandProperty FullName
