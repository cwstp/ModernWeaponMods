# Builds the MWM client BepInEx plugin (Harmony patches only) and deploys it to BepInEx/plugins ONLY.
# Never copied into the Unity SDK project.
$ErrorActionPreference = "Stop"

$src = "D:\SPT\_ModDev\ModernWeaponModsClientBootstrap"
$dest = "D:\SPT\BepInEx\plugins\ModernWeaponModsClient"

Write-Host "Building (Release)..." -ForegroundColor Cyan
dotnet build "$src\ModernWeaponModsClientBootstrap.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$dll = Join-Path $src "bin\Release\ModernWeaponModsClientBootstrap.dll"
if (-not (Test-Path $dll)) { throw "Build did not produce $dll" }

if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest | Out-Null }
Copy-Item $dll (Join-Path $dest "ModernWeaponModsClientBootstrap.dll") -Force

Write-Host "Deployed -> $dest" -ForegroundColor Green
