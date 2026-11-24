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
Copy-Item -LiteralPath $exe -Destination (Join-Path $Target 'DropLink.exe') -Force

$info = Get-Item -LiteralPath (Join-Path $Target 'DropLink.exe')
Write-Output ('OK ' + $info.FullName)
Write-Output ('bytes=' + $info.Length)
