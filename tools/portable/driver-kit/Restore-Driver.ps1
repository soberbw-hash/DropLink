# DropLink - put the RTL8811CU back on its original Realtek driver.
[CmdletBinding()]
param([switch]$NoElevate, [switch]$Force)

$ErrorActionPreference = 'Stop'

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) {
    if ($NoElevate -or $Force) { Write-Host "Administrator rights required."; exit 3 }
    Write-Host "Requesting administrator rights..."
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-NoElevate'
        ) | Out-Null
    } catch { Write-Host "Elevation was refused."; exit 3 }
    exit 0
}

$here = Split-Path -Parent $PSCommandPath
$hardwareId = 'USB\VID_0BDA&PID_C811'

function Get-RadioDevice {
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match 'VID_0BDA&PID_C811' } |
        Select-Object -First 1
}

$device = Get-RadioDevice
if (-not $device) { Write-Host "[X] Adapter not plugged in."; exit 4 }

Write-Host ""
Write-Host "DropLink: restoring the original Wi-Fi driver"
Write-Host ""

$recorded = $null
$jsonPath = Join-Path $here 'original-driver.json'
if (Test-Path $jsonPath) {
    try { $recorded = Get-Content -Raw -Encoding utf8 $jsonPath | ConvertFrom-Json } catch { }
}

Add-Type -Namespace DropLinkDriver -Name Native -MemberDefinition @'
[DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool UpdateDriverForPlugAndPlayDevicesW(
    IntPtr hwndParent, string HardwareId, string FullInfPath,
    uint InstallFlags, out bool bRebootRequired);
'@

$infCandidates = @()
if ($recorded -and $recorded.driverInf) {
    $infCandidates += (Join-Path $env:SystemRoot ("INF\" + $recorded.driverInf))
}
# Any published Realtek net driver is also a valid target.
$infCandidates += Get-ChildItem (Join-Path $env:SystemRoot 'INF') -Filter 'oem*.inf' -ErrorAction SilentlyContinue |
    Where-Object { (Select-String -Path $_.FullName -Pattern 'RtlWlanu|rtwlanu|8811CU' -SimpleMatch -ErrorAction SilentlyContinue) } |
    ForEach-Object { $_.FullName }

$reboot = $false
foreach ($inf in ($infCandidates | Select-Object -Unique)) {
    if (-not (Test-Path $inf)) { continue }
    Write-Host "  trying $inf"
    if ([DropLinkDriver.Native]::UpdateDriverForPlugAndPlayDevicesW(
            [IntPtr]::Zero, $hardwareId, $inf, [uint32]1, [ref]$reboot)) {
        Write-Host "  rebind requested"
    }
}

# Fall back to letting Windows re-enumerate the port.
& pnputil.exe /scan-devices 2>&1 | Out-Null

$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    $current = Get-RadioDevice
    if ($current) {
        $svc = (Get-PnpDeviceProperty -InstanceId $current.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
        if ($svc -and $svc -ne 'WinUSB' -and $current.Status -eq 'OK') {
            Write-Host ""
            Write-Host "[OK] Back on the original driver ($svc)."
            exit 0
        }
    }
    Start-Sleep -Milliseconds 800
}

Write-Host ""
Write-Host "[!] Not restored automatically. Unplug the adapter, plug it back in,"
Write-Host "    then open Device Manager and let Windows install the Realtek driver."
exit 7
