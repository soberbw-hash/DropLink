# DropLink - publish the WPF app after font/XAML changes.
# Pure ASCII so Windows PowerShell 5.1 reads it regardless of system codepage.
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish-font.ps1 `
#     -LiveAppDir 'C:\path\to\DropLink-portable\app'
param([string]$LiveAppDir = $env:DROPLINK_LIVE_APP_DIR)

$ErrorActionPreference = 'Stop'
if (-not $LiveAppDir) {
    throw 'Set -LiveAppDir (or DROPLINK_LIVE_APP_DIR) to the portable app directory.'
}
$repo = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $env:TEMP 'DropLink-publish-stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

# Touch the manifest: MSBuild skips an unchanged file even if its content was
# edited with a preserved mtime, and requireAdministrator would not be embedded.
$manifest = Join-Path $repo 'src\DropLink\app.manifest'
(Get-Item $manifest).LastWriteTime = Get-Date

$proj = Join-Path $repo 'src\DropLink\DropLink.csproj'
dotnet publish $proj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none `
  -o $stage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$newExe = Join-Path $stage 'DropLink.exe'
if (-not (Test-Path $newExe)) { throw 'publish produced no DropLink.exe' }
if (-not (Test-Path $LiveAppDir)) { New-Item -ItemType Directory -Path $LiveAppDir -Force | Out-Null }
$oldExe = Join-Path $LiveAppDir 'DropLink.exe'
if (Test-Path $oldExe) { Copy-Item $oldExe (Join-Path $LiveAppDir 'DropLink.exe.bak-font') -Force }
Copy-Item $newExe $oldExe -Force
Write-Output "published -> $oldExe"
Write-Output ("new size: {0:N1} MB" -f ((Get-Item $oldExe).Length / 1MB))
