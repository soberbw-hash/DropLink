# DropLink test-card - standardized real-device report. Pure ASCII file;
# Chinese UI text is built from Unicode code points at runtime.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$tool = Join-Path $PSScriptRoot 'settings-tool.ps1'
$reportDir = Join-Path $root 'data\test-reports'
if (-not (Test-Path -LiteralPath $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }
function C([int[]]$points) { -join ($points | ForEach-Object { [char]$_ }) }

$L = @{
    Title    = C @(26816,27979,25253,21578)
    DirLbl   = C @(26041,21521,65306)
    ResLbl   = C @(32467,26524,65306)
    FileLbl  = C @(25991,20214,65306)
    Browse   = C @(27983,25269)
    SecLbl   = C @(31195,26102,65306)
    ProbeLbl = C @(32593,32447,26816,27979,65306)
    NoteLbl  = C @(22791,27880,65306)
    Save     = C @(20445,23384,25253,21578)
    Cancel   = C @(21462,28040)
    Success  = C @(25104,21151)
    Fail     = C @(22833,36133)
    Pending  = C @(24453,26580)
    ToPhone  = "Windows -> iPhone/iPad"
    ToPc     = "iPhone/iPad -> Windows"
    Saved    = C @(25253,21578,24050,20445,23384)
}

function Get-DoctorLines {
    try { return @(& $tool -Action doctor 2>&1 | ForEach-Object { [string]$_ }) }
    catch { return @('doctor=failed') }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = $L.Title
$form.Width = 560; $form.Height = 430
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$font = New-Object System.Drawing.Font('Microsoft YaHei', 10)

$lblDir = New-Object System.Windows.Forms.Label; $lblDir.Text = $L.DirLbl; $lblDir.Left = 20; $lblDir.Top = 22; $lblDir.Width = 100; $lblDir.Font = $font
$cmbDir = New-Object System.Windows.Forms.ComboBox; $cmbDir.Left = 125; $cmbDir.Top = 18; $cmbDir.Width = 390; $cmbDir.DropDownStyle = 'DropDownList'; $cmbDir.Font = $font
$null = $cmbDir.Items.Add($L.ToPhone); $null = $cmbDir.Items.Add($L.ToPc); $cmbDir.SelectedIndex = 0

$lblRes = New-Object System.Windows.Forms.Label; $lblRes.Text = $L.ResLbl; $lblRes.Left = 20; $lblRes.Top = 60; $lblRes.Width = 100; $lblRes.Font = $font
$cmbRes = New-Object System.Windows.Forms.ComboBox; $cmbRes.Left = 125; $cmbRes.Top = 56; $cmbRes.Width = 390; $cmbRes.DropDownStyle = 'DropDownList'; $cmbRes.Font = $font
$null = $cmbRes.Items.Add($L.Success); $null = $cmbRes.Items.Add($L.Fail); $null = $cmbRes.Items.Add($L.Pending); $cmbRes.SelectedIndex = 2

$lblFile = New-Object System.Windows.Forms.Label; $lblFile.Text = $L.FileLbl; $lblFile.Left = 20; $lblFile.Top = 98; $lblFile.Width = 100; $lblFile.Font = $font
$txtFile = New-Object System.Windows.Forms.TextBox; $txtFile.Left = 125; $txtFile.Top = 94; $txtFile.Width = 300; $txtFile.Font = $font
$btnBrowse = New-Object System.Windows.Forms.Button; $btnBrowse.Text = $L.Browse; $btnBrowse.Left = 435; $btnBrowse.Top = 92; $btnBrowse.Width = 80; $btnBrowse.Height = 26; $btnBrowse.Font = $font

$lblSec = New-Object System.Windows.Forms.Label; $lblSec.Text = $L.SecLbl; $lblSec.Left = 20; $lblSec.Top = 136; $lblSec.Width = 100; $lblSec.Font = $font
$txtSec = New-Object System.Windows.Forms.TextBox; $txtSec.Left = 125; $txtSec.Top = 132; $txtSec.Width = 390; $txtSec.Font = $font

$lblProbe = New-Object System.Windows.Forms.Label; $lblProbe.Text = $L.ProbeLbl; $lblProbe.Left = 20; $lblProbe.Top = 174; $lblProbe.Width = 100; $lblProbe.Font = $font
$txtProbe = New-Object System.Windows.Forms.TextBox; $txtProbe.Left = 125; $txtProbe.Top = 170; $txtProbe.Width = 390; $txtProbe.Font = $font

$lblNote = New-Object System.Windows.Forms.Label; $lblNote.Text = $L.NoteLbl; $lblNote.Left = 20; $lblNote.Top = 212; $lblNote.Width = 100; $lblNote.Font = $font
$txtNote = New-Object System.Windows.Forms.TextBox; $txtNote.Multiline = $true; $txtNote.Left = 125; $txtNote.Top = 208; $txtNote.Width = 390; $txtNote.Height = 110; $txtNote.Font = $font

$btnSave = New-Object System.Windows.Forms.Button; $btnSave.Text = $L.Save; $btnSave.Left = 125; $btnSave.Top = 335; $btnSave.Width = 190; $btnSave.Height = 34; $btnSave.Font = $font
$btnCancel = New-Object System.Windows.Forms.Button; $btnCancel.Text = $L.Cancel; $btnCancel.Left = 325; $btnCancel.Top = 335; $btnCancel.Width = 190; $btnCancel.Height = 34; $btnCancel.Font = $font

$btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    if ($dlg.ShowDialog() -eq 'OK') { $txtFile.Text = $dlg.FileName }
})
$btnCancel.Add_Click({ $form.Close() })
$btnSave.Add_Click({
    $seconds = 0.0
    if (-not [double]::TryParse($txtSec.Text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$seconds)) { $seconds = 0.0 }
    $size = 0L
    $hash = ''
    if ($txtFile.Text -and (Test-Path -LiteralPath $txtFile.Text -PathType Leaf)) {
        $item = Get-Item -LiteralPath $txtFile.Text
        $size = $item.Length
        try { $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $txtFile.Text).Hash } catch { $hash = '' }
    }
    $mbps = 0.0
    if ($size -gt 0 -and $seconds -gt 0) { $mbps = ($size / 1000000.0) / $seconds }
    $report = [ordered]@{
        schema_version = 1
        generated_at = (Get-Date -Format s)
        direction = $cmbDir.SelectedItem
        result = $cmbRes.SelectedItem
        file_path = $txtFile.Text
        file_size_bytes = $size
        duration_seconds = $seconds
        avg_decimal_MBps = [math]::Round($mbps, 2)
        sha256 = $hash
        wifi_probes = $txtProbe.Text
        notes = $txtNote.Text
        doctor = @(Get-DoctorLines)
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $path = Join-Path $reportDir ('report-' + $stamp + '.json')
    $json = $report | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
    $txtNote.Text = $path
    [System.Windows.Forms.MessageBox]::Show($L.Saved + "`n" + $path)
    $form.Close()
})
[System.Windows.Forms.Application]::Run($form)
