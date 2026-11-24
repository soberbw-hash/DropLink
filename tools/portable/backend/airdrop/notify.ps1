# DropLink - Windows toast notification for a received file.
# All text is UTF-8 base64 on the command line. This file is pure ASCII so
# Windows PowerShell 5.1 reads it correctly regardless of system codepage.
param(
    [Parameter(Mandatory=$true)][string]$FolderB64,
    [string]$TitleB64 = '~',
    [string]$MessageB64 = '~',
    [string]$ClipB64 = '~',
    [string]$OpenLabelB64 = '~',
    [string]$ClipLabelB64 = '~',
    [string]$ImageB64 = '~'
)
$ErrorActionPreference = 'Stop'

# Our own AppUserModelID. Borrowing someone else's (the old code used
# Microsoft.Windows.Explorer) makes Windows label the toast with that app's
# name and icon, and silently refuses to load a local file:// image because
# the notification platform has no identity to resolve it against.
$Aumid = 'DropLink.Receiver'
$FallbackAumid = 'Microsoft.Windows.Explorer'

function From-B64([string]$s) {
    # PowerShell drops an empty argument from the command line entirely, so an
    # empty string can never be passed in: '-MessageB64 ""' binds to nothing and
    # the whole call fails. '~' is the agreed stand-in for "empty" (a bare
    # '-' would be read as the start of another parameter name).
    if (-not $s -or $s -eq '~') { return '' }
    try { return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($s)) }
    catch { return '' }
}
function To-UrlB64([string]$s) {
    if (-not $s) { return '' }
    $b = [System.Text.Encoding]::UTF8.GetBytes($s)
    return ([System.Convert]::ToBase64String($b)).Replace('+','-').Replace('/','_').TrimEnd('=')
}

# Register the identity so the toast is attributed to DropLink with its icon.
# HKCU\Software\Classes\AppUserModelId is the documented way for a desktop app
# without an MSIX package to own a toast identity.
function Ensure-Aumid {
    try {
        $key = 'HKCU:\Software\Classes\AppUserModelId\' + $Aumid
        if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
        New-ItemProperty -Path $key -Name 'DisplayName' -Value 'DropLink' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $key -Name 'IconBackgroundColor' -Value 'FF1F6FEB' -PropertyType String -Force | Out-Null
        $exe = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'app\DropLink.exe'
        if (Test-Path -LiteralPath $exe) {
            New-ItemProperty -Path $key -Name 'IconUri' -Value $exe -PropertyType String -Force | Out-Null
        }
    } catch { }
}

function Ensure-Protocol {
    try {
        $here = Split-Path -Parent $PSCommandPath
        # wscript.exe, not powershell.exe: see register-protocol.ps1. A console
        # program registered here flashes a black window on every button click.
        $handler = Join-Path $here 'toast-action.vbs'
        $root = 'HKCU:\Software\Classes\droplink'
        if (-not (Test-Path $root)) { New-Item -Path $root -Force | Out-Null }
        New-ItemProperty -Path $root -Name '(Default)' -Value 'URL:DropLink toast action' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $root -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
        $cmdPath = "$root\shell\open\command"
        if (-not (Test-Path $cmdPath)) { New-Item -Path $cmdPath -Force | Out-Null }
        $command = '"C:\Windows\System32\wscript.exe" //nologo //B "{0}" "%1"' -f $handler
        New-ItemProperty -Path $cmdPath -Name '(Default)' -Value $command -PropertyType String -Force | Out-Null
    } catch { }
}

# Copy the preview next to the profile instead of pointing at the receive
# folder. Three reasons: the received file may be moved or renamed before the
# toast renders; the install path can contain non-ASCII characters, which the
# toast host does not reliably percent-decode; and a camera original is far
# past the size the toast host will accept, so it has to be re-encoded small.
function Stage-Preview([string]$source) {
    if (-not $source) { return '' }
    if (-not (Test-Path -LiteralPath $source)) { return '' }
    $dir = Join-Path $env:LOCALAPPDATA 'DropLink\toast'
    try { New-Item -ItemType Directory -Path $dir -Force | Out-Null } catch { return '' }
    # Keep the folder from growing forever; the toast only needs seconds.
    try {
        Get-ChildItem -LiteralPath $dir -Filter 'preview-*' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -Skip 6 |
            Remove-Item -Force -ErrorAction SilentlyContinue
    } catch { }

    $target = Join-Path $dir ('preview-' + [guid]::NewGuid().ToString('N') + '.jpg')
    try {
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
        $img = [System.Drawing.Image]::FromFile($source)
        try {
            $max = 320.0
            $scale = [Math]::Min(1.0, [Math]::Min($max / $img.Width, $max / $img.Height))
            $w = [int][Math]::Max(1, [Math]::Round($img.Width * $scale))
            $h = [int][Math]::Max(1, [Math]::Round($img.Height * $scale))
            $bmp = New-Object System.Drawing.Bitmap $w, $h
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            # JPEG has no alpha; fill white first so transparent PNGs do not
            # come out with black corners.
            $g.Clear([System.Drawing.Color]::White)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.DrawImage($img, 0, 0, $w, $h)
            $g.Dispose()
            $bmp.Save($target, [System.Drawing.Imaging.ImageFormat]::Jpeg)
            $bmp.Dispose()
        } finally { $img.Dispose() }
        if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -gt 0) { return $target }
    } catch { }

    # Fallback for formats System.Drawing cannot decode (HEIC without the
    # codec): hand the original over untouched, but only when it is small
    # enough that the toast host will still accept it.
    try {
        if ((Get-Item -LiteralPath $source).Length -le 2000000) {
            $ext = [System.IO.Path]::GetExtension($source)
            if (-not $ext) { $ext = '.png' }
            $raw = Join-Path $dir ('preview-' + [guid]::NewGuid().ToString('N') + $ext)
            Copy-Item -LiteralPath $source -Destination $raw -Force
            return $raw
        }
    } catch { }
    return ''
}

try {
    $folder  = From-B64 $FolderB64
    $title   = From-B64 $TitleB64
    $message = From-B64 $MessageB64
    $clip    = From-B64 $ClipB64
    $openLbl = From-B64 $OpenLabelB64
    $clipLbl = From-B64 $ClipLabelB64
    $imagePath = Stage-Preview (From-B64 $ImageB64)
    if (-not $openLbl) { $openLbl = 'Open folder' }
    if (-not $clipLbl) { $clipLbl = 'Copy to clipboard' }

    Ensure-Aumid
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
    if ($message) {
        $texts.Item(1).InnerText = $message
    } else {
        # An empty <text/> still reserves a blank line under the title. Drop
        # the node so a one-line toast actually looks like one line.
        $spare = $texts.Item(1)
        $spare.ParentNode.RemoveChild($spare) | Out-Null
    }
    # Optional inline preview image (e.g. the received photo). hint-crop=none
    # keeps the whole frame instead of centre-cropping it into a square.
    if ($imagePath) {
        try {
            $img = $doc.CreateElement('image')
            $img.SetAttribute('placement', 'inline')
            $img.SetAttribute('hint-crop', 'none')
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
    try {
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($Aumid).Show($toast)
    } catch {
        # A machine that refuses the custom identity must still show something,
        # but the reason has to be visible or this fallback hides itself.
        try {
            [System.IO.File]::AppendAllText(
                (Join-Path $PSScriptRoot 'notify-error.log'),
                ("{0} aumid {1} rejected: {2}`n" -f (Get-Date -Format s), $Aumid, $_.Exception.Message))
        } catch { }
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($FallbackAumid).Show($toast)
    }
} catch {
    [System.IO.File]::AppendAllText(
        (Join-Path $PSScriptRoot 'notify-error.log'),
        ("{0} {1}`n" -f (Get-Date -Format s), $_.Exception.Message))
}
