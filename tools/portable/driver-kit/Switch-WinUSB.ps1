# DropLink - one-time switch of the RTL8811CU (USB\VID_0BDA&PID_C811) to WinUSB.
# Fully automatic: builds a WinUSB driver package, signs it with a local test
# certificate, installs it, then forces the device onto it. No Zadig clicking.
# Reversible: run 回退驱动.cmd (Restore-Driver.ps1).
[CmdletBinding()]
param(
    [switch]$NoElevate,
    [switch]$Force,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'

# Logging is set up before anything else: an aborted run must still leave a
# trace, otherwise a silent failure is indistinguishable from never starting.
$here = Split-Path -Parent $PSCommandPath
$logFile = Join-Path $here 'setup.log'
function Write-Log($text) {
    try { Add-Content -Path $logFile -Encoding utf8 -Value ("{0} {1}" -f (Get-Date -Format s), $text) } catch { }
}

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step($text) { Write-Host ("  " + $text) }

$elevated = Test-Elevated
Write-Log ("--- start pid={0} elevated={1} force={2} checkOnly={3} ---" -f $PID, $elevated, $Force, $CheckOnly)

if (-not $elevated) {
    if ($Force -or $NoElevate) {
        Write-Host "This script needs administrator rights."
        Write-Host "Close this window and start DropLink from its Start DropLink.cmd launcher."
        Write-Log "abort: not elevated"
        exit 3
    }
    Write-Host "Requesting administrator rights..."
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
        ) | Out-Null
    } catch {
        Write-Host "Elevation was refused: $($_.Exception.Message)"
        Write-Log "abort: elevation refused - $($_.Exception.Message)"
        exit 3
    }
    exit 0
}
Write-Host ""
Write-Host "DropLink: preparing the RTL8811CU adapter for AirDrop"
Write-Host ""

# ---------------------------------------------------------------- 1. find it
function Get-RadioDevice {
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match 'VID_0BDA&PID_C811' } |
        Select-Object -First 1
}

$device = Get-RadioDevice
if (-not $device) {
    Write-Host "[X] The RTL8811CU adapter (0BDA:C811) is not plugged in."
    Write-Host "    Plug it into a rear USB port and run this again."
    exit 4
}

function Get-ServiceName($instanceId) {
    (Get-PnpDeviceProperty -InstanceId $instanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
}

$service = Get-ServiceName $device.InstanceId
Write-Step "Adapter : $($device.FriendlyName)"
Write-Step "Driver  : $service"
Write-Log "instance=$($device.InstanceId) service=$service status=$($device.Status)"

if ($CheckOnly) {
    if ($service -eq 'WinUSB') { Write-Host "Already WinUSB."; exit 0 }
    Write-Host "Not WinUSB yet."
    exit 1
}

if ($service -eq 'WinUSB' -and $device.Status -eq 'OK') {
    Write-Host ""
    Write-Host "[OK] Already switched. Nothing to do."
    Write-Log "already winusb"
    exit 0
}

# Remember what to restore later.
$originalInf = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data
$originalVersion = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverVersion' -ErrorAction SilentlyContinue).Data
$original = [ordered]@{
    instanceId    = $device.InstanceId
    hardwareId    = 'USB\VID_0BDA&PID_C811'
    friendlyName  = $device.FriendlyName
    service       = $service
    driverInf     = $originalInf
    driverVersion = $originalVersion
    savedAt       = (Get-Date -Format s)
}
$originalPath = Join-Path $here 'original-driver.json'
if (-not (Test-Path $originalPath)) {
    $original | ConvertTo-Json | Set-Content -Encoding utf8 $originalPath
    Write-Step "Backed up: $originalInf"
    Write-Log "backup=$originalInf"
}

# ------------------------------------------------- 2. build the driver package
$package = Join-Path $here 'package'
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item -ItemType Directory -Path $package | Out-Null

$guid = '{D6A5B0C4-3E7F-4A85-9C21-8F41A2B7E5D1}'
$inf = @"
[Version]
Signature="`$Windows NT`$"
Class=USBDevice
ClassGuid={88BAE032-5A81-49f0-BC3D-A4FF138216D6}
Provider=%Provider%
CatalogFile=DropLinkWinUSB.cat
DriverVer=11/24/2026,1.0.0.0

[Manufacturer]
%Provider%=DeviceList,NTamd64

[DeviceList.NTamd64]
%DeviceName%=DropLinkInstall,USB\VID_0BDA&PID_C811

[DropLinkInstall]
Include=winusb.inf
Needs=WINUSB.NT
AddReg=DropLinkAddReg

[DropLinkInstall.Services]
Include=winusb.inf
Needs=WINUSB.NT.Services

[DropLinkAddReg]
HKR,,DeviceInterfaceGUIDs,0x10000,"$guid"

[Strings]
Provider="DropLink"
DeviceName="DropLink WinUSB (RTL8811CU)"
"@

$infPath = Join-Path $package 'DropLinkWinUSB.inf'
# INF files must be UTF-16LE with BOM for non-ASCII safety; this one is ASCII
# but SetupAPI accepts UTF-16 everywhere, so write it that way.
[System.IO.File]::WriteAllText($infPath, $inf, (New-Object System.Text.UnicodeEncoding($false, $true)))

# ------------------------------------------------------- 3. signing material
Write-Step "Signing driver package..."
$subject = 'CN=DropLink WinUSB Driver'
$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -eq $subject } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert -or $cert.NotAfter -lt (Get-Date).AddDays(30)) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
}
$cerPath = Join-Path $here 'DropLinkWinUSB.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
foreach ($store in 'Root', 'TrustedPublisher') {
    $target = "Cert:\LocalMachine\$store"
    if (-not (Get-ChildItem $target | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
        Import-Certificate -FilePath $cerPath -CertStoreLocation $target | Out-Null
        Write-Log "cert imported into $store"
    }
}

# Catalog must hash every file in the folder, so create it last.
$catPath = Join-Path $package 'DropLinkWinUSB.cat'
New-FileCatalog -Path $package -CatalogFilePath $catPath -CatalogVersion 2 | Out-Null
Set-AuthenticodeSignature -FilePath $catPath -Certificate $cert -HashAlgorithm SHA256 | Out-Null
if ((Get-AuthenticodeSignature $catPath).Status -ne 'Valid') {
    Write-Host "[X] Could not sign the driver package."
    Write-Log "cat signature invalid"
    exit 5
}

# ------------------------------------------------------------ 4. install it
Write-Step "Installing driver package..."
$add = & pnputil.exe /add-driver "$infPath" /install 2>&1 | Out-String
$addExit = $LASTEXITCODE
Write-Log "pnputil add-driver (exit=$addExit): $add"
# pnputil returns a non-zero code in two very different situations: the package
# was really refused, and the harmless "package already present / the driver on
# the device is up to date" case (it reports 0 packages added). Only a run that
# produced no oemNN.inf name actually failed. Treating the second case as a
# failure aborted before the FORCE binding below and left the adapter on its
# original driver, which is exactly what "Windows refused the driver package"
# used to mean on a machine where the package was already in the store.
$published = if ($add -match '(oem\d+\.inf)') { $Matches[1] } else { $null }
if ($published) { Write-Log "published as $published" }
if (-not $published) {
    Write-Host "[X] Windows refused the driver package."
    Write-Host $add.Trim()
    exit 6
}

# ------------------------------------------------------- 5. force the binding
Add-Type -Namespace DropLinkDriver -Name Native -MemberDefinition @'
[DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool UpdateDriverForPlugAndPlayDevicesW(
    IntPtr hwndParent, string HardwareId, string FullInfPath,
    uint InstallFlags, out bool bRebootRequired);
'@

Write-Step "Binding adapter to WinUSB..."
$reboot = $false
# NOTE: do not name this $force -- PowerShell variable names are case
# insensitive, so it would assign to the [switch]$Force parameter and fail with
# "cannot convert System.UInt32 to SwitchParameter".
$installFlags = [uint32]1   # INSTALLFLAG_FORCE
$ok = [DropLinkDriver.Native]::UpdateDriverForPlugAndPlayDevicesW(
    [IntPtr]::Zero, 'USB\VID_0BDA&PID_C811', $infPath, $installFlags, [ref]$reboot)
Write-Log "UpdateDriverForPlugAndPlayDevices=$ok"

if (-not $ok) {
    # Second attempt without FORCE, then rescan: some builds rank the in-box
    # winusb.inf above our package and need a re-enumeration instead.
    Write-Step "Retrying the binding..."
    $retry = & pnputil.exe /add-driver "$infPath" /install /force 2>&1 | Out-String
    Write-Log "pnputil retry (exit=$LASTEXITCODE): $retry"
    & pnputil.exe /scan-devices 2>&1 | Out-Null
    Start-Sleep -Seconds 2
}

# ------------------------------------------------------------- 6. verify
$deadline = (Get-Date).AddSeconds(60)
$final = $null
while ((Get-Date) -lt $deadline) {
    $current = Get-RadioDevice
    if ($current) {
        $now = Get-ServiceName $current.InstanceId
        if ($now -eq 'WinUSB' -and $current.Status -eq 'OK') { $final = $current; break }
    }
    Start-Sleep -Milliseconds 800
}

Write-Host ""
if ($final) {
    Write-Host "[OK] Done. The adapter is now a WinUSB device."
    Write-Host "     DropLink refreshes itself; this window closes on its own."
    Write-Log "verified winusb"
    exit 0
}

Write-Host "[X] The adapter did not come back as WinUSB."
Write-Host "    Unplug it and plug it back in, then run this file again."
Write-Host "    If it still fails, run 回退驱动.cmd and open zadig-2.9.exe."
Write-Log "verify failed"
exit 7
