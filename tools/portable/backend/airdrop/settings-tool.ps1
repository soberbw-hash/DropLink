# DropLink settings helper - pure ASCII, Windows PowerShell 5.1 safe.
# Chinese strings are built from code points so the file stays ASCII.
param([Parameter(Mandatory=$true)][string]$Action, [string]$Value = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$settingsPath = Join-Path $root 'data\settings.json'
$hardwarePath = Join-Path $root 'data\hardware.json'
$logPath = Join-Path $root 'data\logs\engine.log'

function C([int[]]$points) { -join ($points | ForEach-Object { [char]$_ }) }
$msg = @{
    On = C @(23458,26426,21551,21160); Off = C @(20851,38381,23458,26426,21551,21160)
    Name = C @(35774,22791,21517,31228); BadName = C @(21517,31228,19981,33021,20026,31354)
    Check = C @(26816,26597,39537,21160); Ok = C @(39537,21160,24050,32500,20284)
    Drv = C @(39537,21160,29366,24577)
}

$autostartTask = 'DropLink'
function Get-AutostartState {
    try {
        $task = Get-ScheduledTask -TaskName $autostartTask -ErrorAction SilentlyContinue
        if ($task) { return 'on' }
    } catch { }
    $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'DropLink.lnk'
    if (Test-Path -LiteralPath $lnk) { return 'on' }
    return 'off'
}
function Remove-LegacyAutostart {
    # Older builds used a Startup-folder shortcut. It shows a console flash and a
    # UAC prompt at logon, so the scheduled task replaces it.
    $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'DropLink.lnk'
    if (Test-Path -LiteralPath $lnk) { Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue }
}

function Read-JsonFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return @{} }
    try { return (Get-Content -Raw -Encoding utf8 -LiteralPath $path | ConvertFrom-Json) } catch { return @{} }
}
function Read-Settings { return Read-JsonFile $settingsPath }
function Write-Settings($obj) {
    $json = $obj | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
}
function Prop([string]$instance, [string]$key) {
    try { return (Get-PnpDeviceProperty -InstanceId $instance -KeyName $key -ErrorAction SilentlyContinue).Data }
    catch { return '' }
}
function FileState([string]$path) {
    if (Test-Path -LiteralPath $path) { return 'OK' }
    return 'MISSING'
}
function ProcState([string]$name) {
    $p = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($p) { return ('RUNNING:' + ($p.Id -join ',')) }
    return 'STOPPED'
}
function TcpState([int]$port) {
    try {
        $rows = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue
        if (-not $rows) { return 'NONE' }
        return (($rows | ForEach-Object { $_.State }) -join ',')
    } catch { return 'UNKNOWN' }
}
function UdpState([int]$port) {
    try {
        $rows = Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue
        if (-not $rows) { return 'NONE' }
        return 'BOUND'
    } catch { return 'UNKNOWN' }
}
function OsLabel {
    try {
        $cv = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        return (($cv.ProductName) + ' ' + $cv.CurrentBuild + '.' + $cv.UBR)
    } catch { return ([Environment]::OSVersion.VersionString) }
}
function Run-Doctor {
    $s = Read-Settings
    $h = Read-JsonFile $hardwarePath
    Write-Output ('time=' + (Get-Date -Format s))
    Write-Output ('computer=' + $env:COMPUTERNAME)
    Write-Output ('os=' + (OsLabel))
    Write-Output ('root=' + $root)
    Write-Output ('app.exe=' + (FileState (Join-Path $root 'app\DropLink.exe')))
    Write-Output ('backend.radio=' + (FileState (Join-Path $root 'backend\airbridge_radio.exe')))
    Write-Output ('backend.bridge=' + (FileState (Join-Path $root 'backend\radio_pipe_bridge.exe')))
    Write-Output ('backend.netd=' + (FileState (Join-Path $root 'backend\awdl_netd.exe')))
    Write-Output ('backend.awdld=' + (FileState (Join-Path $root 'backend\awdld.exe')))
    Write-Output ('backend.python=' + (FileState (Join-Path $root 'backend\python\python.exe')))
    Write-Output ('backend.engine=' + (FileState (Join-Path $root 'backend\airdrop\engine.py')))
    Write-Output ('settings.file=' + (FileState $settingsPath))
    Write-Output ('settings.device_name=' + $s.device_name)
    Write-Output ('settings.auto_receive=' + $s.auto_receive)
    Write-Output ('settings.live_copy=' + $s.live_photo_still_copy)
    Write-Output ('settings.format_hint=' + $s.show_format_in_notification)
    Write-Output ('autostart=' + (Get-AutostartState))
    Write-Output ('hardware.file=' + (FileState $hardwarePath))
    Write-Output ('hardware.allow_untested=' + $h.allow_untested)
    if ($h.devices) { Write-Output ('hardware.devices=' + @($h.devices).Count) } else { Write-Output 'hardware.devices=0' }
    $nics = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VID_0BDA&PID_C811|VID_0BDA&PID_C820' })
    Write-Output ('nic.count=' + $nics.Count)
    for ($i = 0; $i -lt $nics.Count; $i++) {
        $d = $nics[$i]
        Write-Output ('nic[' + $i + '].instance=' + $d.InstanceId)
        Write-Output ('nic[' + $i + '].service=' + (Prop $d.InstanceId 'DEVPKEY_Device_Service'))
        Write-Output ('nic[' + $i + '].status=' + $d.Status)
        Write-Output ('nic[' + $i + '].inf=' + (Prop $d.InstanceId 'DEVPKEY_Device_DriverInfPath'))
        Write-Output ('nic[' + $i + '].driver=' + (Prop $d.InstanceId 'DEVPKEY_Device_DriverVersion'))
        Write-Output ('nic[' + $i + '].location=' + (Prop $d.InstanceId 'DEVPKEY_Device_LocationInfo'))
    }
    Write-Output ('link.tcp8771=' + (TcpState 8771))
    Write-Output ('link.udp15353=' + (UdpState 15353))
    Write-Output ('proc.radio=' + (ProcState 'airbridge_radio'))
    Write-Output ('proc.bridge=' + (ProcState 'radio_pipe_bridge'))
    Write-Output ('proc.netd=' + (ProcState 'awdl_netd'))
    Write-Output ('proc.awdld=' + (ProcState 'awdld'))
    Write-Output ('proc.engine=' + (ProcState 'python'))
    Write-Output ('log.file=' + (FileState $logPath))
    if (Test-Path -LiteralPath $logPath) {
        $item = Get-Item -LiteralPath $logPath
        Write-Output ('log.bytes=' + $item.Length)
        Write-Output ('log.updated=' + $item.LastWriteTime.ToString('s'))
    } else {
        Write-Output 'log.bytes=0'
        Write-Output 'log.updated=none'
    }
}

function Get-DoctorData {
    $s = Read-Settings
    $h = Read-JsonFile $hardwarePath
    $nics = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VID_0BDA&PID_C811|VID_0BDA&PID_C820' })
    $nicItems = @()
    foreach ($d in $nics) {
        $nicItems += [pscustomobject]@{
            instance = [string]$d.InstanceId
            service  = [string](Prop $d.InstanceId 'DEVPKEY_Device_Service')
            status   = [string]$d.Status
            inf      = [string](Prop $d.InstanceId 'DEVPKEY_Device_DriverInfPath')
            driver   = [string](Prop $d.InstanceId 'DEVPKEY_Device_DriverVersion')
            location = [string](Prop $d.InstanceId 'DEVPKEY_Device_LocationInfo')
        }
    }
    $log = if (Test-Path -LiteralPath $logPath) {
        $item = Get-Item -LiteralPath $logPath
        [pscustomobject]@{ file = 'OK'; bytes = $item.Length; updated = $item.LastWriteTime.ToString('s') }
    } else {
        [pscustomobject]@{ file = 'MISSING'; bytes = 0; updated = $null }
    }
    [pscustomobject]@{
        schema   = 'droplink.doctor/1'
        time     = (Get-Date -Format s)
        computer = $env:COMPUTERNAME
        os       = (OsLabel)
        root     = $root
        app      = [pscustomobject]@{ exe = (FileState (Join-Path $root 'app\DropLink.exe')) }
        backend  = [pscustomobject]@{
            radio  = (FileState (Join-Path $root 'backend\airbridge_radio.exe'))
            bridge = (FileState (Join-Path $root 'backend\radio_pipe_bridge.exe'))
            netd   = (FileState (Join-Path $root 'backend\awdl_netd.exe'))
            awdld  = (FileState (Join-Path $root 'backend\awdld.exe'))
            python = (FileState (Join-Path $root 'backend\python\python.exe'))
            engine = (FileState (Join-Path $root 'backend\airdrop\engine.py'))
        }
        settings = [pscustomobject]@{
            file         = (FileState $settingsPath)
            device_name  = [string]$s.device_name
            auto_receive = $s.auto_receive
            live_copy    = $s.live_photo_still_copy
            format_hint  = $s.show_format_in_notification
        }
        hardware = [pscustomobject]@{
            file           = (FileState $hardwarePath)
            allow_untested = $h.allow_untested
            devices        = @($h.devices).Count
        }
        autostart = (Get-AutostartState)
        nics      = $nicItems
        link      = [pscustomobject]@{ tcp8771 = (TcpState 8771); udp15353 = (UdpState 15353) }
        processes = [pscustomobject]@{
            radio  = (ProcState 'airbridge_radio')
            bridge = (ProcState 'radio_pipe_bridge')
            netd   = (ProcState 'awdl_netd')
            awdld  = (ProcState 'awdld')
            engine = (ProcState 'python')
        }
        log = $log
    }
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
    'autostart-state' {
        Write-Output (Get-AutostartState)
    }
    'autostart-on' {
        # A logon scheduled task with the highest run level starts the receiver
        # elevated without a UAC prompt, so the tray appears quietly.
        $exe = Join-Path $root 'app\DropLink.exe'
        if (-not (Test-Path -LiteralPath $exe)) { throw ('missing ' + $exe) }
        $taskAction = New-ScheduledTaskAction -Execute $exe -Argument '--tray' -WorkingDirectory (Split-Path -Parent $exe)
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
        $userId = [string]$env:USERDOMAIN + '\' + [string]$env:USERNAME
        $principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
        Register-ScheduledTask -TaskName $autostartTask -Action $taskAction -Trigger $trigger `
            -Principal $principal -Settings $settings -Description 'DropLink AirDrop receiver' -Force | Out-Null
        Remove-LegacyAutostart
        Write-Output ('OK ' + $msg.On)
    }
    'autostart-off' {
        Unregister-ScheduledTask -TaskName $autostartTask -Confirm:$false -ErrorAction SilentlyContinue
        Remove-LegacyAutostart
        Write-Output ('OK ' + $msg.Off)
    }
    'checkdriver' {
        $dev = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VID_0BDA&PID_C811' } | Select-Object -First 1
        if (-not $dev) { Write-Output ("NO_CARD " + $msg.Check); exit 2 }
        $svc = (Get-PnpDeviceProperty -InstanceId $dev.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
        Write-Output ("service=" + $svc)
        Write-Output ("status=" + $dev.Status)
        if ($svc -eq 'WinUSB' -and $dev.Status -eq 'OK') { Write-Output ("READY " + $msg.Ok) } else { Write-Output ("NOT_READY " + $msg.Check) }
    }
    'doctor' { Run-Doctor }
    'doctor-json' { Get-DoctorData | ConvertTo-Json -Depth 6 }
    'export-diagnostics' {
        # One zip a user can send for support: logs, doctor snapshot, settings.
        # Never includes data\keys (TLS material) or received files.
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $logDir = Join-Path $root 'data\logs'
        $items = @()
        foreach ($name in @('app.log', 'app.log.1', 'engine.log', 'engine-stderr.log', 'engine-stderr.log.1')) {
            $p = Join-Path $logDir $name
            if (Test-Path -LiteralPath $p) { $items += $p }
        }
        foreach ($name in @('settings.json', 'hardware.json')) {
            $p = Join-Path $root ('data\' + $name)
            if (Test-Path -LiteralPath $p) { $items += $p }
        }
        if (Test-Path -LiteralPath $logDir) {
            $doctorPath = Join-Path $logDir ('doctor-' + $stamp + '.json')
            try {
                (Get-DoctorData | ConvertTo-Json -Depth 6) |
                    Set-Content -LiteralPath $doctorPath -Encoding utf8
                $items += $doctorPath
            } catch { }
        }
        if ($items.Count -eq 0) { throw 'nothing to export' }
        $out = Join-Path $root ('data\diagnostics-' + $stamp + '.zip')
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
        Compress-Archive -LiteralPath $items -DestinationPath $out -Force
        Write-Output ('OK ' + $out)
    }
    'testcard' {
        $card = Join-Path $PSScriptRoot 'test-card.ps1'
        if (-not (Test-Path -LiteralPath $card)) { throw 'test-card.ps1 is missing' }
        & $card
    }
    default { throw "unknown action: $Action" }
}
