# DropLink benchmark report - pure ASCII, Windows PowerShell 5.1 safe.
# Records file size, direction, channel, width, elapsed time, decimal MB/s,
# Wi-Fi probe results and SHA-256. Real AirDrop timing is entered by the user.
param(
    [string]$File = '',
    [string]$Direction = 'windows-to-iphone',
    [double]$Seconds = 0,
    [int]$Channel = 149,
    [int]$WidthMHz = 80,
    [string]$GatewayProbes = '',
    [string]$InternetProbes = '',
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$reportDir = Join-Path $root 'data\test-reports'
if (-not (Test-Path -LiteralPath $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

if (-not $File) { $File = Read-Host 'File path' }
if ($Seconds -le 0) {
    $raw = Read-Host 'File body seconds'
    $null = [double]::TryParse($raw, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$Seconds)
}
if (-not $GatewayProbes) { $GatewayProbes = Read-Host 'Gateway probes (e.g. 6/6)' }
if (-not $InternetProbes) { $InternetProbes = Read-Host 'Internet probes (e.g. 40/40)' }

if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "File not found: $File" }
$item = Get-Item -LiteralPath $File
$size = $item.Length
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $File).Hash
$mbps = 0.0
if ($Seconds -gt 0) { $mbps = ($size / 1000000.0) / $Seconds }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$base = Join-Path $reportDir ('benchmark-' + $stamp)
$report = [ordered]@{
    schema_version = 1
    generated_at = (Get-Date -Format s)
    computer = $env:COMPUTERNAME
    os = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').ProductName
    direction = $Direction
    channel = $Channel
    width_mhz = $WidthMHz
    file_path = $File
    file_size_bytes = $size
    duration_seconds = $Seconds
    avg_decimal_MBps = [math]::Round($mbps, 2)
    gateway_probes = $GatewayProbes
    internet_probes = $InternetProbes
    sha256 = $hash
    notes = $Notes
}
$jsonPath = $base + '.json'
$mdPath = $base + '.md'
[System.IO.File]::WriteAllText($jsonPath, ($report | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
$md = @(
    '# DropLink benchmark',
    '',
    ('- Generated: ' + $report.generated_at),
    ('- Direction: ' + $Direction),
    ('- Channel / width: ' + $Channel + ' / ' + $WidthMHz + ' MHz'),
    ('- File: ' + $File),
    ('- Size: ' + $size + ' bytes'),
    ('- File body seconds: ' + $Seconds),
    ('- Average: ' + $report.avg_decimal_MBps + ' decimal MB/s'),
    ('- Wi-Fi gateway probes: ' + $GatewayProbes),
    ('- Wi-Fi internet probes: ' + $InternetProbes),
    ('- SHA-256: ' + $hash),
    ('- Notes: ' + $Notes),
    '',
    'Timing starts at file body and ends at file body; discovery and acceptance are excluded.'
)
[System.IO.File]::WriteAllLines($mdPath, $md, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ('JSON=' + $jsonPath)
Write-Output ('MARKDOWN=' + $mdPath)
Write-Output ('AVG_MBps=' + $report.avg_decimal_MBps)
