# Construye el instalador de DOCS-DR con Velopack.
#
#   powershell -File scripts/build-installer.ps1 -Version 1.0.0
#
# Resultado en artifacts/releases:
#   DocsDR-win-Setup.exe           instalador para los usuarios
#   DocsDR-<versión>-full.nupkg    paquete completo (lo usan las actualizaciones automáticas)
#   DocsDR-<versión>-delta.nupkg   solo las diferencias con la versión anterior (si existe)
#   releases.win.json              índice que lee la app para saber si hay una versión nueva
#
# Para que la app se actualice sola, todos estos archivos se suben a GitHub Releases de la versión
# (ver README, sección "Publicar una versión").
param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'artifacts\publish'
$releases = Join-Path $root 'artifacts\releases'
$env:Path += ";$env:USERPROFILE\.dotnet\tools"

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "Falta la herramienta vpk. Instálala con: dotnet tool install -g vpk"
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force $releases | Out-Null

Write-Host "1/2 Publicando DOCS-DR $Version (autocontenido, win-x64)..."
dotnet publish (Join-Path $root 'src\DocsDR.App\DocsDR.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación" }

Write-Host "2/2 Creando el instalador con Velopack..."
vpk pack `
    --packId DocsDR `
    --packVersion $Version `
    --packDir $publish `
    --mainExe DocsDR.exe `
    --runtime win-x64 `
    --packTitle 'DOCS-DR' `
    --packAuthors 'DRPCS E.A.S.' `
    --icon (Join-Path $root 'src\DocsDR.App\Assets\icon.ico') `
    --shortcuts 'Desktop,StartMenu' `
    --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "Falló vpk pack" }

Write-Host ""
Write-Host "Listo. Archivos en $releases"
Get-ChildItem $releases | Select-Object Name, @{n='MB'; e={[math]::Round($_.Length / 1MB, 1)}} | Format-Table -AutoSize
