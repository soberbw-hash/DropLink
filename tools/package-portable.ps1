# Assembles the delivery zip from an assembled portable folder.
#
# Why this exists: the package was zipped by hand, and the hand-picked result
# carried things that must never leave this machine -
#   data\keys        the TLS private key. Shipping it means every copy of the
#                    product presents the same key pair. receiver.py generates
#                    one on first use (identity.ensure_self_signed), so the
#                    correct shipped state is "no keys folder".
#   data\logs        this machine's logs.
#   data\received    files this machine received.
#   data\settings.json  absolute paths from this machine.
#   _backup\old      stale exe images, ~83 MB.
#   _backup\droplink-driver\original-driver.json
#                    the driver recorded on THIS machine. Switch-WinUSB.ps1
#                    only writes it when absent, so shipping it stops a new
#                    machine from recording its own, and Restore-Driver.ps1
#                    would aim at the wrong INF.
#   backend\awdl_netd.addr  a runtime address file.
#
# Run check-portable.py on the source folder first; a package built from a
# broken folder is still broken.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
    throw "source folder not found: $Source"
}
$Source = (Resolve-Path -LiteralPath $Source).Path
if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $OutFile = Join-Path (Split-Path -Parent $Source) 'DropLink-portable.zip'
}

# Folder names kept at the root, in order. Anything else at the root is copied
# as a plain file (the readme and the launcher shortcut).
$rootFolders = @('app', 'backend', 'data', 'licenses', '_backup')

# data\ is an allow-list, not a deny-list: everything under it is either
# machine state or something generated at runtime, and a new name appearing
# there later must not silently end up in a release.
$dataKeep = @('hardware.json', 'hardware.default.json', 'settings.default.json')

# Relative to the source root. Wildcards are allowed for files.
$skipDirs = @(
    'data\keys',
    'data\logs',
    'data\received',
    'data\test-reports',
    '_backup\old',
    '_backup\droplink-driver\package'
)
$skipFiles = @(
    'data\settings.json',
    'data\diagnostics-*.zip',
    '_backup\droplink-driver\original-driver.json',
    '_backup\droplink-driver\setup.log',
    'backend\awdl_netd.addr',
    '*.old',
    '*.bak-*',
    # The package root is the repository root now, so the version-control
    # metadata sits next to the files that ship. A recipient unzipping the
    # delivery does not need to know this was a git checkout.
    '.gitignore',
    '.gitattributes'
)

function Test-Skip([string]$relative) {
    foreach ($dir in $skipDirs) {
        if ($relative -eq $dir -or $relative.StartsWith($dir + '\')) { return $true }
    }
    foreach ($pattern in $skipFiles) {
        if ($relative -like $pattern) { return $true }
    }
    return $false
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('droplink-pack-' + [Guid]::NewGuid().ToString('N'))
$target = Join-Path $staging 'DropLink'
New-Item -ItemType Directory -Path $target -Force | Out-Null

$copied = 0
$skipped = New-Object System.Collections.Generic.List[string]

try {
    foreach ($folder in $rootFolders) {
        $from = Join-Path $Source $folder
        if (-not (Test-Path -LiteralPath $from -PathType Container)) {
            Write-Host "missing folder: $folder"
            continue
        }
        $files = Get-ChildItem -LiteralPath $from -Recurse -File -Force
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($Source.Length + 1)
            if ($folder -eq 'data' -and $dataKeep -notcontains $file.Name) {
                $skipped.Add($relative + '  (data allow-list)')
                continue
            }
            if (Test-Skip $relative) {
                $skipped.Add($relative)
                continue
            }
            $dest = Join-Path $target $relative
            $parent = Split-Path -Parent $dest
            if (-not (Test-Path -LiteralPath $parent)) {
                New-Item -ItemType Directory -Path $parent -Force | Out-Null
            }
            Copy-Item -LiteralPath $file.FullName -Destination $dest -Force
            $copied++
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Force) {
        if (Test-Skip $file.Name) {
            $skipped.Add($file.Name)
            continue
        }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $file.Name) -Force
        $copied++
    }

    # An empty folder left behind by the copy loop would still ship as an empty
    # directory and read as "something is missing here".
    Get-ChildItem -LiteralPath $target -Recurse -Directory -Force |
        Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0 } |
        Remove-Item -Force

    $exe = Join-Path $target 'app\DropLink.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'staged package has no app\DropLink.exe' }

    if (Test-Path -LiteralPath $OutFile) { Remove-Item -LiteralPath $OutFile -Force }
    $outParent = Split-Path -Parent $OutFile
    if ($outParent -and -not (Test-Path -LiteralPath $outParent)) {
        New-Item -ItemType Directory -Path $outParent -Force | Out-Null
    }
    Compress-Archive -LiteralPath $target -DestinationPath $OutFile -CompressionLevel Optimal

    $info = Get-Item -LiteralPath $OutFile
    $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    Write-Host ''
    Write-Host ("files copied : {0}" -f $copied)
    Write-Host ("files skipped: {0}" -f $skipped.Count)
    foreach ($item in $skipped) { Write-Host ("  - " + $item) }
    Write-Host ''
    Write-Host ("package : " + $info.FullName)
    Write-Host ("size    : {0:N1} MB" -f ($info.Length / 1MB))
    Write-Host ("exe ver : " + $version)
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
