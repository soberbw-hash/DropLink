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
try {
    Copy-Item -LiteralPath $exe -Destination $dest -Force
} catch {
    # A running DropLink holds its own image, so the copy fails. That used to be
    # worked around by renaming the old exe to .old, which left the user clicking
    # a stale build and wondering why nothing changed. Refuse loudly instead:
    # a publish that does not land is worse than no publish at all.
    $pids = @(Get-Process -Name 'DropLink' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    $who = ''
    if ($pids.Count -gt 0) { $who = ' (running pid ' + ($pids -join ', ') + ')' }
    throw ('cannot replace ' + $dest + ': DropLink is running' + $who + '. Quit DropLink first, then publish again.')
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
foreach ($extra in @('DropLink.ico', 'icon-source.png')) {
    $path = Join-Path $Target $extra
    if (Test-Path -LiteralPath $path) {
        Write-Output ($extra + '=' + (Get-Item -LiteralPath $path).Length)
    } else {
        Write-Output ($extra + '=MISSING')
    }
}
