# DropLink - handles droplink: protocol activation from toast buttons.
# Usage: toast-action.ps1 "droplink:clip?text=<b64>&folder=<b64>"
#        toast-action.ps1 "droplink:open?folder=<b64>&file=<b64>"
# Pure ASCII file; all real text is UTF-8 base64 in the URI.
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
try {
    $text = From-B64 (Get-Param $Uri 'text')
    $folder = From-B64 (Get-Param $Uri 'folder')
    $file = From-B64 (Get-Param $Uri 'file')
    if ($Uri -match '^droplink:clip' -and $text) {
        # The payload is a list of full paths, one per line. Copy the files
        # themselves, not their names: "复制到剪贴板" on a photo is supposed to
        # end with the photo in the clipboard, ready to paste into a chat.
        $paths = @($text -split "`n" | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
        if ($paths.Count -gt 0) {
            Set-Clipboard -Path $paths
            Note ("copied {0} file(s)" -f $paths.Count)
        } else {
            # Nothing on disk any more: at least hand over the text.
            Set-Clipboard -Value $text
            Note 'copied text: no received file still exists'
        }
    } elseif ($Uri -match '^droplink:open' -and $folder) {
        if ($file) {
            $target = Join-Path $folder $file
            if (Test-Path -LiteralPath $target) {
                # Select the received file instead of just opening the folder.
                # Opening a folder full of UUID-named photos and telling the user
                # "it is in there" is how a received photo looks lost.
                Start-Process explorer.exe -ArgumentList ('/select,"{0}"' -f $target)
                Note ("opened folder and selected {0}" -f $file)
                return
            }
            Note ("file not found: {0}" -f $target)
        }
        if (Test-Path -LiteralPath $folder) {
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
