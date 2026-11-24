# DropLink - Windows toast notification for a received file.
# All text is UTF-8 base64 on the command line. This file is pure ASCII so
# Windows PowerShell 5.1 reads it correctly regardless of system codepage.
param(
    [Parameter(Mandatory=$true)][string]$FolderB64,
    [string]$TitleB64 = '',
    [string]$MessageB64 = '',
    [string]$ClipB64 = '',
    [string]$OpenLabelB64 = '',
    [string]$ClipLabelB64 = '',
    [string]$ImageB64 = ''
)
$ErrorActionPreference = 'Stop'
function From-B64([string]$s) {
    if (-not $s) { return '' }
    try { return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($s)) }
    catch { return '' }
}
function To-UrlB64([string]$s) {
    if (-not $s) { return '' }
    $b = [System.Text.Encoding]::UTF8.GetBytes($s)
    return ([System.Convert]::ToBase64String($b)).Replace('+','-').Replace('/','_').TrimEnd('=')
}
function Ensure-Protocol {
    try {
        $here = Split-Path -Parent $PSCommandPath
        $handler = Join-Path $here 'toast-action.ps1'
        $root = 'HKCU:\Software\Classes\droplink'
        if (-not (Test-Path $root)) { New-Item -Path $root -Force | Out-Null }
        New-ItemProperty -Path $root -Name '(Default)' -Value 'URL:DropLink toast action' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $root -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
        $cmdPath = "$root\shell\open\command"
        if (-not (Test-Path $cmdPath)) { New-Item -Path $cmdPath -Force | Out-Null }
        $command = '"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "{0}" "%1"' -f $handler
        New-ItemProperty -Path $cmdPath -Name '(Default)' -Value $command -PropertyType String -Force | Out-Null
    } catch { }
}
try {
    $folder  = From-B64 $FolderB64
    $title   = From-B64 $TitleB64
    $message = From-B64 $MessageB64
    $clip    = From-B64 $ClipB64
    $openLbl = From-B64 $OpenLabelB64
    $clipLbl = From-B64 $ClipLabelB64
    $imagePath = From-B64 $ImageB64
    if (-not $openLbl) { $openLbl = 'Open folder' }
    if (-not $clipLbl) { $clipLbl = 'Copy to clipboard' }

    Ensure-Protocol

    $null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
    $null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]

    $openUri = 'droplink:open?folder=' + (To-UrlB64 $folder)
    $clipUri = 'droplink:clip?text=' + (To-UrlB64 $clip) + '&folder=' + (To-UrlB64 $folder)

    $doc = [Windows.Data.Xml.Dom.XmlDocument]::new()
    # URIs contain '&' which must not be hand-spliced into XML. Build a plain
    # skeleton, then set every attribute through the DOM API so escaping is
    # handled by Windows.
    $doc.LoadXml('<toast><visual><binding template="ToastGeneric"><text/><text/></binding></visual><actions><action/><action/></actions></toast>')
    $root = $doc.DocumentElement
    $root.SetAttribute('activation', 'protocol')
    $root.SetAttribute('launch', $openUri)
    $root.SetAttribute('duration', 'long')
    $texts = $doc.GetElementsByTagName('text')
    $texts.Item(0).InnerText = $title
    $texts.Item(1).InnerText = $message
    # Optional inline preview image (e.g. the received photo).
    if ($imagePath -and (Test-Path $imagePath)) {
        try {
            $img = $doc.CreateElement('image')
            $img.SetAttribute('placement', 'inline')
            $img.SetAttribute('src', ([System.Uri]::new($imagePath)).AbsoluteUri)
            $binding = $doc.GetElementsByTagName('binding').Item(0)
            $binding.AppendChild($img) | Out-Null
        } catch { }
    }
    $acts = $doc.GetElementsByTagName('action')
    $acts.Item(0).SetAttribute('content', $openLbl)
    $acts.Item(0).SetAttribute('activationType', 'protocol')
    $acts.Item(0).SetAttribute('arguments', $openUri)
    $acts.Item(1).SetAttribute('content', $clipLbl)
    $acts.Item(1).SetAttribute('activationType', 'protocol')
    $acts.Item(1).SetAttribute('arguments', $clipUri)

    $toast = [Windows.UI.Notifications.ToastNotification]::new($doc)
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Microsoft.Windows.Explorer').Show($toast)
} catch {
    [System.IO.File]::AppendAllText(
        (Join-Path $PSScriptRoot 'notify-error.log'),
        ("{0} {1}`n" -f (Get-Date -Format s), $_.Exception.Message))
}
