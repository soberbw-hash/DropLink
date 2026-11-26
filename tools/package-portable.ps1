# Assemble a relocatable user download from a published Windows package.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Source, [string]$OutFile = '')

$ErrorActionPreference = 'Stop'
$Source = (Get-Item -LiteralPath $Source).FullName
if (!$OutFile) { $OutFile = Join-Path (Split-Path -Parent $Source) 'DropLink-portable.zip' }
$OutFile = [IO.Path]::GetFullPath($OutFile)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutFile)) | Out-Null
$temp = $OutFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Add-File([string]$path, [string]$entry) {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $path,
        ('DropLink/' + $entry.Replace('\','/')), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
function Add-Runtime([string]$relative) {
    Add-File (Join-Path $Source $relative) ('runtime/' + $relative)
}

try {
    $zip = [IO.Compression.ZipFile]::Open($temp, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-File (Join-Path $Source 'app/DropLink.exe') 'DropLink.exe'
        $manual = @(Get-ChildItem -LiteralPath "$PSScriptRoot/portable" -Filter '*.txt' -File)
        if ($manual.Count -ne 1) { throw 'Expected one portable manual.' }
        Add-File $manual[0].FullName $manual[0].Name
        foreach ($asset in @('DropLink.ico','DropLink-toast.png')) { Add-Runtime ('app/' + $asset) }
        foreach ($binary in @('airbridge_radio.exe','radio_pipe_bridge.exe','awdl_netd.exe','awdld.exe','libusb-1.0.dll','msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')) {
            Add-Runtime ('backend/' + $binary)
        }
        # Only the named scripts in the shipped template, never runtime logs or keys.
        foreach ($file in Get-ChildItem -LiteralPath "$PSScriptRoot/portable/backend/airdrop" -File) {
            if ($file.Extension -in @('.py','.ps1','.vbs')) { Add-Runtime ('backend/airdrop/' + $file.Name) }
        }
        foreach ($file in Get-ChildItem -LiteralPath "$Source/backend/python" -File -Recurse) {
            if ($file.FullName -match '\\__pycache__\\' -or $file.Extension -in @('.pyc','.log') -or $file.Name -eq 'direct_url.json') { continue }
            Add-Runtime $file.FullName.Substring($Source.Length + 1)
        }
        foreach ($file in Get-ChildItem -LiteralPath "$PSScriptRoot/portable/driver-kit" -File) {
            Add-Runtime ('_backup/droplink-driver/' + $file.Name)
        }
        foreach ($name in @('settings.default.json','hardware.default.json')) {
            Add-File "$PSScriptRoot/portable/data/$name" ('runtime/data/' + $name)
        }
        Add-File "$PSScriptRoot/portable/data/hardware.default.json" 'runtime/data/hardware.json'
        $licenses = @(Get-ChildItem -LiteralPath "$Source/licenses" -File -Filter '*.txt')
        if (!$licenses.Count) { throw 'Missing third-party licenses.' }
        foreach ($file in $licenses) { Add-File $file.FullName ('runtime/licenses/' + $file.Name) }
    } finally { $zip.Dispose() }
    if ([IO.File]::Exists($OutFile)) {
        $previous = $temp + '.previous'
        [IO.File]::Replace($temp,$OutFile,$previous)
        [IO.File]::Delete($previous)
    }
    else { [IO.File]::Move($temp,$OutFile) }
    Get-Item -LiteralPath $OutFile | Select-Object FullName,Length
} finally {
    if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
}
