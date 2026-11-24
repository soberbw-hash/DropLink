# DropLink - handles droplink: protocol activation from toast buttons.
# Usage: toast-action.ps1 "droplink:clip?text=<b64>&folder=<b64>"
#        toast-action.ps1 "droplink:open?folder=<b64>"
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
try {
    $text = From-B64 (Get-Param $Uri 'text')
    $folder = From-B64 (Get-Param $Uri 'folder')
    if ($Uri -match '^droplink:clip' -and $text) {
        Set-Clipboard -Value $text
    } elseif ($Uri -match '^droplink:open' -and $folder) {
        if (Test-Path $folder) { Start-Process explorer.exe -ArgumentList ('"{0}"' -f $folder) }
    }
} catch {
    [System.IO.File]::AppendAllText(
        (Join-Path $PSScriptRoot 'notify-error.log'),
        ("{0} action: {1}`n" -f (Get-Date -Format s), $_.Exception.Message))
}
