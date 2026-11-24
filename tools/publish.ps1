# Publishes the WPF receiver as one self-contained executable.
# Pure ASCII, Windows PowerShell 5.1 safe.
param(
    [string]$Target = '',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Target)) {
    $Target = Join-Path $repo 'tools\portable\app'
}

$project = Join-Path $repo 'src\DropLink\DropLink.csproj'
$stage = Join-Path $repo 'out\publish'

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }

$dotnetArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $stage
)
if (-not $FrameworkDependent) { $dotnetArgs += @('--self-contained', 'true') } else { $dotnetArgs += @('--self-contained', 'false') }

& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw ('publish failed with exit code ' + $LASTEXITCODE) }

$exe = Join-Path $stage 'DropLink.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw ('missing ' + $exe) }

if (-not (Test-Path -LiteralPath $Target)) { New-Item -ItemType Directory -Path $Target -Force | Out-Null }
$dest = Join-Path $Target 'DropLink.exe'
# A running DropLink holds its own image, so it cannot be overwritten - but it
# can be renamed, which is how an update lands without asking the user to quit
# first. The renamed copy is only ever used by the process that is already
# running; the file on disk is the new build. It is cleaned up here on the next
# publish, once nothing holds it.
$old = Join-Path $Target 'DropLink.exe.old'
if (Test-Path -LiteralPath $old) {
    try { Remove-Item -LiteralPath $old -Force } catch { }
}
$renamed = $false
try {
    Copy-Item -LiteralPath $exe -Destination $dest -Force
} catch {
    try {
        Move-Item -LiteralPath $dest -Destination $old -Force
        Copy-Item -LiteralPath $exe -Destination $dest -Force
        $renamed = $true
    } catch {
        $pids = @(Get-Process -Name 'DropLink' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
        $who = ''
        if ($pids.Count -gt 0) { $who = ' (running pid ' + ($pids -join ', ') + ')' }
        throw ('cannot replace ' + $dest + ': DropLink is running' + $who + '. Quit DropLink first, then publish again.')
    }
}

# Ship the icon next to the executable, not only inside it. The .ico is what a
# shortcut or a dock needs, and the source PNG is the design file: if either
# only ever existed on the build machine, it is one disk failure away from gone.
$icon = Join-Path $repo 'src\DropLink\app.ico'
$art = Join-Path $repo 'tools\icon-source.png'
if (Test-Path -LiteralPath $icon) {
    Copy-Item -LiteralPath $icon -Destination (Join-Path $Target 'DropLink.ico') -Force
}
if (Test-Path -LiteralPath $art) {
    Copy-Item -LiteralPath $art -Destination (Join-Path $Target 'icon-source.png') -Force
}

$info = Get-Item -LiteralPath (Join-Path $Target 'DropLink.exe')
Write-Output ('OK ' + $info.FullName)
Write-Output ('bytes=' + $info.Length)
if ($renamed) { Write-Output ('note=replaced while running; old image renamed to ' + $old) }
foreach ($extra in @('DropLink.ico', 'icon-source.png')) {
    $path = Join-Path $Target $extra
    if (Test-Path -LiteralPath $path) {
        Write-Output ($extra + '=' + (Get-Item -LiteralPath $path).Length)
    } else {
        Write-Output ($extra + '=MISSING')
    }
}
