# DropLink - handles droplink: protocol activation from toast buttons.
# Usage: toast-action.ps1 "droplink:clip?batch=<b64>&text=<b64>&folder=<b64>"
#        toast-action.ps1 "droplink:open?batch=<b64>&folder=<b64>&file=<b64>"
# Pure ASCII file; all real text is UTF-8 base64 in the URI.
#
# The buttons carry a history id. A protocol URI is world-invokable - any
# process, or a web page through the shell, can hand this script a path, so a
# path in the URI must never be enough to aim Explorer somewhere. The id is
# resolved against the local history first; the URI path is only honoured when
# it points inside the configured receive folder.
param([Parameter(Mandatory=$true)][string]$Uri)
$ErrorActionPreference = 'Stop'
function Get-Param([string]$u, [string]$name) {
    $m = [regex]::Match($u, "(?:[?&])$name=([^&]+)")
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}
function From-B64([string]$s) {
    if (-not $s) { return '' }
    try {
        $s = $s.Replace('-', '+').Replace('_', '/')
        while ($s.Length % 4) { $s += '=' }
        return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($s))
    } catch { return '' }
}
# A button that silently does nothing is worse than one that is missing: the
# user cannot tell "nothing to open" from "this feature is broken". Every
# outcome lands in this log.
function Note([string]$message) {
    try {
        [System.IO.File]::AppendAllText(
            (Join-Path $PSScriptRoot 'toast-action.log'),
            ("{0} {1}`n" -f (Get-Date -Format s), $message))
    } catch { }
}
function Get-DataPath([string]$name) {
    $root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    return Join-Path (Join-Path $root 'data') $name
}
function Get-Setting([string]$key) {
    try {
        $path = Get-DataPath 'settings.json'
        if (-not (Test-Path -LiteralPath $path)) { return '' }
        $cfg = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        $value = $cfg.$key
        if ($null -eq $value) { return '' }
        return [string]$value
    } catch { return '' }
}
# The batch the toast was shown for, as the receiver recorded it. This is the
# only source of paths the handler trusts.
function Get-Batch([string]$id) {
    if (-not $id) { return $null }
    try {
        $path = Get-DataPath 'history.json'
        if (-not (Test-Path -LiteralPath $path)) { return $null }
        $rows = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($row in @($rows)) {
            if ($row -and ([string]$row.Id) -eq $id) { return $row }
        }
    } catch { Note ("history unreadable: {0}" -f $_.Exception.Message) }
    return $null
}
function Same-Folder([string]$a, [string]$b) {
    if (-not $a -or -not $b) { return $false }
    try {
        return ([System.IO.Path]::GetFullPath($a).TrimEnd('\') -ieq
                [System.IO.Path]::GetFullPath($b).TrimEnd('\'))
    } catch { return $false }
}
# Paths handed over in the URI are only usable when they sit inside the folder
# this handler already trusts. Anything else is somebody else's file.
function Inside-Folder([string]$path, [string]$folder) {
    if (-not $path -or -not $folder) { return $false }
    try {
        $full = [System.IO.Path]::GetFullPath($path)
        $root = [System.IO.Path]::GetFullPath($folder).TrimEnd('\') + '\'
        return $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}
try {
    $batch  = From-B64 (Get-Param $Uri 'batch')
    $text   = From-B64 (Get-Param $Uri 'text')
    $folder = From-B64 (Get-Param $Uri 'folder')
    $file   = From-B64 (Get-Param $Uri 'file')

    $row = Get-Batch $batch
    $names = @()
    if ($row) {
        $folder = [string]$row.Folder
        $names = @($row.Files | Where-Object { $_ } | ForEach-Object { [string]$_ })
    } elseif (Same-Folder $folder (Get-Setting 'receive_folder')) {
        # History was cleared or lost, but the path is inside the receive
        # folder - the only folder this handler is allowed to touch.
        if ($file) { $names = @($file) }
    } else {
        $folder = Get-Setting 'receive_folder'
        Note ("batch {0} unknown and folder not trusted; using the receive folder" -f $batch)
    }

    $existing = @($names | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $folder $_)) })

    if ($Uri -match '^droplink:clip') {
        # The payload is a list of received files. Copy the files themselves,
        # not their names: "复制到剪贴板" on a photo is supposed to end with the
        # photo in the clipboard, ready to paste into a chat.
        $paths = @($existing | ForEach-Object { Join-Path $folder $_ } |
                   Where-Object { Test-Path -LiteralPath $_ })
        if ($paths.Count -eq 0 -and $text) {
            # The URI payload is only a fallback for a machine whose history was
            # wiped, so it is filtered down to the trusted folder first. Taking
            # it at face value would let any caller put any file on the
            # clipboard just by writing a droplink: URI.
            $paths = @($text -split "`n" |
                       Where-Object { $_ -and (Inside-Folder $_ $folder) -and (Test-Path -LiteralPath $_) })
        }
        if ($paths.Count -gt 0) {
            Set-Clipboard -Path $paths
            Note ("copied {0} file(s) for batch {1}" -f $paths.Count, $batch)
        } else {
            Note ("nothing to copy for batch {0}" -f $batch)
        }
    } elseif ($Uri -match '^droplink:open') {
        if ($existing.Count -eq 1) {
            $target = Join-Path $folder $existing[0]
            # Select the received file instead of just opening the folder.
            # Opening a folder full of UUID-named photos and telling the user
            # "it is in there" is how a received photo looks lost.
            Start-Process explorer.exe -ArgumentList ('/select,"{0}"' -f $target)
            Note ("opened folder and selected {0}" -f $existing[0])
            return
        }
        if ($existing.Count -gt 1) {
            Note ("batch {0} has {1} files; opening the folder" -f $batch, $existing.Count)
        }
        if ($folder -and (Test-Path -LiteralPath $folder)) {
            Start-Process explorer.exe -ArgumentList ('"{0}"' -f $folder)
            Note ("opened folder {0}" -f $folder)
        } else {
            Note ("folder not found: {0}" -f $folder)
        }
    } else {
        Note ("nothing to do for {0}" -f $Uri)
    }
} catch {
    [System.IO.File]::AppendAllText(
        (Join-Path $PSScriptRoot 'notify-error.log'),
        ("{0} action: {1}`n" -f (Get-Date -Format s), $_.Exception.Message))
}
