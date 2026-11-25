# Publishes the WPF receiver as one self-contained executable.
# Pure ASCII, Windows PowerShell 5.1 safe.
param(
    [string]$Target = '',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Target)) {
    # The shipping package and the source tree are the same folder now: repo\app
    # holds the exe, repo\backend holds the backends. Only a checkout that has no
    # backends beside it falls back to the old separate staging tree.
    if (Test-Path -LiteralPath (Join-Path $repo 'backend\airbridge_radio.exe')) {
        $Target = Join-Path $repo 'app'
    } else {
        $Target = Join-Path $repo 'tools\portable\app'
    }
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

# The engine and the PowerShell helpers live beside the backends, not inside the
# exe. Publishing only the exe left a package running last week's engine after a
# rebuild, which reads as "the fix did not work". Sync them whenever the target
# sits in a real package root - one whose backend\ actually holds the radio, so
# the template tree under tools\portable is not touched.
$packageRoot = Split-Path -Parent $Target
$runtimeBackend = Join-Path $packageRoot 'backend'
if (Test-Path -LiteralPath (Join-Path $runtimeBackend 'airbridge_radio.exe')) {
    $scriptSource = Join-Path $repo 'tools\portable\backend\airdrop'
    $scriptTarget = Join-Path $runtimeBackend 'airdrop'
    if (Test-Path -LiteralPath $scriptSource) {
        if (-not (Test-Path -LiteralPath $scriptTarget)) {
            New-Item -ItemType Directory -Path $scriptTarget -Force | Out-Null
        }
        $copied = 0
        foreach ($item in Get-ChildItem -LiteralPath $scriptSource -File) {
            Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $scriptTarget $item.Name) -Force
            $copied++
        }
        Write-Output ('engine=' + $copied + ' file(s) synced to ' + $scriptTarget)
    }

    # The driver kit drifts the same way the manual did: the shipped copy under
    # _backup is what the user actually double-clicks, and the one that gets
    # fixed is the one under tools\portable. A stale copy there is worse than a
    # stale manual - it fails at the step where the machine stops being able to
    # see the network card at all.
    $kitSource = Join-Path $repo 'tools\portable\driver-kit'
    $kitTarget = Join-Path $packageRoot '_backup\droplink-driver'
    if ((Test-Path -LiteralPath $kitSource) -and (Test-Path -LiteralPath $kitTarget)) {
        $kitCopied = 0
        foreach ($item in Get-ChildItem -LiteralPath $kitSource -File) {
            $dest = Join-Path $kitTarget $item.Name
            if ((Test-Path -LiteralPath $dest) -and
                ((Get-FileHash -LiteralPath $item.FullName).Hash -eq (Get-FileHash -LiteralPath $dest).Hash)) {
                continue
            }
            Copy-Item -LiteralPath $item.FullName -Destination $dest -Force
            $kitCopied++
        }
        Write-Output ('driver-kit=' + $kitCopied + ' file(s) updated')
    }

    # The user manual ships at the package root and is generated, not authored
    # there: tools\portable holds the copy that gets edited. Two hand-maintained
    # copies is how the shipped text ended up telling people to launch through a
    # script the program no longer needs. Matched by extension rather than by
    # name so this file stays pure ASCII - PowerShell 5.1 reads a BOM-less script
    # as ANSI, and a Chinese literal here would arrive as mojibake.
    $manualSource = @(Get-ChildItem -LiteralPath (Join-Path $repo 'tools\portable') -Filter '*.txt' -File -ErrorAction SilentlyContinue)
    if ($manualSource.Count -eq 1) {
        $manualTarget = Join-Path $packageRoot $manualSource[0].Name
        if ((Test-Path -LiteralPath $manualTarget) -and
            ((Get-FileHash -LiteralPath $manualSource[0].FullName).Hash -eq (Get-FileHash -LiteralPath $manualTarget).Hash)) {
            Write-Output 'manual=unchanged'
        } else {
            Copy-Item -LiteralPath $manualSource[0].FullName -Destination $manualTarget -Force
            Write-Output ('manual=synced to ' + $manualTarget)
        }
    } else {
        Write-Output ('manual=skipped (' + $manualSource.Count + ' .txt files in tools\portable)')
    }
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
