# DropLink settings helper - pure ASCII, Windows PowerShell 5.1 safe.
# Chinese strings are built from code points so the file stays ASCII.
param([Parameter(Mandatory=$true)][string]$Action, [string]$Value = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$settingsPath = Join-Path $root 'data\settings.json'

function C([int[]]$points) { -join ($points | ForEach-Object { [char]$_ }) }
$msg = @{
    On = C @(23458,26426,21551,21160); Off = C @(20851,38381,23458,26426,21551,21160)
    Name = C @(35774,22791,21517,31228); BadName = C @(21517,31228,19981,33021,20026,31354)
    Check = C @(26816,26597,39537,21160); Ok = C @(39537,21160,24050,32500,20284)
    Drv = C @(39537,21160,29366,24577)
}

function Read-Settings {
    if (-not (Test-Path $settingsPath)) { return @{} }
    try { return (Get-Content -Raw -Encoding utf8 $settingsPath | ConvertFrom-Json) } catch { return @{} }
}
function Write-Settings($obj) {
    $json = $obj | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
}

switch ($Action) {
    'get' {
        $s = Read-Settings
        Write-Output ("name=" + $s.device_name)
        Write-Output ("auto=" + $s.auto_receive)
    }
    'setname' {
        if ([string]::IsNullOrWhiteSpace($Value)) { throw $msg.BadName }
        $s = Read-Settings
        $s | Add-Member -NotePropertyName device_name -NotePropertyValue $Value -Force
        Write-Settings $s
        Write-Output ("OK name=" + $Value)
    }
    'autostart-on' {
        $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'DropLink.lnk'
        $target = Join-Path $root '启动 DropLink.cmd'
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut($lnk)
        $sc.TargetPath = $target
        $sc.WorkingDirectory = $root
        $sc.Description = 'DropLink'
        $sc.Save()
        Write-Output ("OK " + $msg.On)
    }
    'autostart-off' {
        $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'DropLink.lnk'
        if (Test-Path $lnk) { Remove-Item $lnk -Force }
        Write-Output ("OK " + $msg.Off)
    }
    'checkdriver' {
        $dev = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VID_0BDA&PID_C811' } | Select-Object -First 1
        if (-not $dev) { Write-Output ("NO_CARD " + $msg.Check); exit 2 }
        $svc = (Get-PnpDeviceProperty -InstanceId $dev.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
        Write-Output ("service=" + $svc)
        Write-Output ("status=" + $dev.Status)
        if ($svc -eq 'WinUSB' -and $dev.Status -eq 'OK') { Write-Output ("READY " + $msg.Ok) } else { Write-Output ("NOT_READY " + $msg.Check) }
    }
    default { throw "unknown action: $Action" }
}
