# Baut das MSI-Paket unter Windows.
#   .\installer\build-installer.ps1 -Version 1.0.1
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "1/4 Tests"
dotnet test tests/LiftUp.Core.Tests/LiftUp.Core.Tests.csproj -c $Configuration | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Tests fehlgeschlagen." }

Write-Host "2/4 Veröffentlichen (framework-abhängig, schlank)"
dotnet publish src/LiftUp.App/LiftUp.App.csproj -c $Configuration -r $Runtime --self-contained false `
    -p:Version=$Version -p:DebugType=none -o artifacts/publish | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Veröffentlichen fehlgeschlagen." }

# Prüfwerkzeug für die Datenbanken mit ins Paket legen (wird auch vom Setup genutzt).
dotnet publish tools/LiftUp.Preflight/LiftUp.Preflight.csproj -c $Configuration -r $Runtime --self-contained false `
    -p:Version=$Version -p:DebugType=none -o artifacts/publish | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Veröffentlichen des Prüfwerkzeugs fehlgeschlagen." }

Write-Host "3/4 WiX bereitstellen"
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    dotnet tool install --global wix --version 5.0.2 | Out-Host
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}
wix extension add -g WixToolset.UI.wixext/5.0.2 2>&1 | Out-Host

Write-Host "4/4 MSI bauen"
$publishDir = (Resolve-Path artifacts/publish).Path
wix build installer/LiftUp.wxs `
    -d Version=$Version `
    -d PublishDir="$publishDir" `
    -d IconPath="$(Resolve-Path src/LiftUp.App/Assets/LiftUp.ico).Path" `
    -d LicensePath="$(Resolve-Path installer/License.rtf).Path" `
    -d BannerPath="$(Resolve-Path installer/assets/banner.bmp).Path" `
    -d DialogPath="$(Resolve-Path installer/assets/dialog.bmp).Path" `
    -d GitHubOwner="soendi" `
    -d GitHubRepo="LiftUp" `
    -ext WixToolset.UI.wixext `
    -culture de-de `
    -o artifacts/LiftUp-Setup.msi | Out-Host

$hash = (Get-FileHash artifacts/LiftUp-Setup.msi -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  LiftUp-Setup.msi" | Out-File -FilePath artifacts/SHA256SUMS.txt -Encoding ascii

Write-Host "Fertig: artifacts/LiftUp-Setup.msi ($Version)"
