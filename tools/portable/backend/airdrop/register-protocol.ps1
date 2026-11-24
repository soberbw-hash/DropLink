# DropLink - register the droplink: URL protocol for toast button actions.
# Runs per-user (HKCU), no administrator rights required. Pure ASCII file.
$ErrorActionPreference = 'Stop'
try {
    $here = Split-Path -Parent $PSCommandPath
    # wscript.exe, not powershell.exe: a protocol activation starts a console
    # program with a visible window, so registering powershell here made every
    # toast button click flash a black console. The .vbs host has no console and
    # starts the worker hidden.
    $handler = Join-Path $here 'toast-action.vbs'
    $root = 'HKCU:\Software\Classes\droplink'
    if (-not (Test-Path $root)) { New-Item -Path $root -Force | Out-Null }

    New-ItemProperty -Path $root -Name '(Default)' -Value 'URL:DropLink toast action' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $root -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null

    $cmdPath = "$root\shell\open\command"
    if (-not (Test-Path $cmdPath)) { New-Item -Path $cmdPath -Force | Out-Null }
    # %1 is the full droplink:... URI passed by Windows on activation.
    $command = '"C:\Windows\System32\wscript.exe" //nologo //B "{0}" "%1"' -f $handler
    New-ItemProperty -Path $cmdPath -Name '(Default)' -Value $command -PropertyType String -Force | Out-Null
    Write-Output "registered droplink protocol -> $handler"
} catch {
    [System.IO.File]::AppendAllText(
        (Join-Path $PSScriptRoot 'notify-error.log'),
        ("{0} protocol: {1}`n" -f (Get-Date -Format s), $_.Exception.Message))
}
