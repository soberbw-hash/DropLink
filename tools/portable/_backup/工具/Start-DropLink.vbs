' DropLink portable launcher - stable alternative to "start" from CMD.
' Pure ASCII; run with wscript.exe. Sets all DROPLINK_* variables then
' starts the elevated app and leaves it in the taskbar.
Option Explicit
Dim WshShell, fso, root, appDir, exe, backend, driver, dataDir, logDir, logFile, ts
Dim radio, bridge, netd, awdld, pythonExe, engine, setupCmd, rollbackCmd
Dim net, missing, extra, arg

Set WshShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

' This file sits two folders below the package root (_backup\工具\), so the
' root is two levels up, not one.
root    = fso.GetParentFolderName(fso.GetParentFolderName(WScript.ScriptFullName)) & "\"
appDir  = root & "app\"
exe     = appDir & "DropLink.exe"
backend = root & "backend\"
driver  = root & "_backup\droplink-driver\"
dataDir = root & "data\"
logDir  = dataDir & "logs\"

If Not fso.FolderExists(logDir) Then fso.CreateFolder logDir
Set ts = fso.OpenTextFile(logDir & "launch.log", 8, True)
ts.WriteLine "=== launch " & Now & " ==="
ts.WriteLine "root=" & root

If Not fso.FileExists(exe) Then
  ts.WriteLine "ABORT: exe missing"
  ts.Close
  MsgBox "DropLink.exe not found:" & vbCrLf & exe, 16, "DropLink"
  WScript.Quit 1
End If

radio      = backend & "airbridge_radio.exe"
bridge     = backend & "radio_pipe_bridge.exe"
netd       = backend & "awdl_netd.exe"
awdld      = backend & "awdld.exe"
pythonExe  = backend & "python\python.exe"
engine     = backend & "airdrop\engine.py"
setupCmd   = driver & "Switch-WinUSB.ps1"
rollbackCmd = driver & "Restore-Driver.ps1"

missing = ""
If Not fso.FileExists(radio)     Then missing = missing & "  airbridge_radio.exe" & vbCrLf
If Not fso.FileExists(bridge)    Then missing = missing & "  radio_pipe_bridge.exe" & vbCrLf
If Not fso.FileExists(netd)      Then missing = missing & "  awdl_netd.exe" & vbCrLf
If Not fso.FileExists(awdld)     Then missing = missing & "  awdld.exe" & vbCrLf
If Not fso.FileExists(pythonExe) Then missing = missing & "  python\python.exe" & vbCrLf
If Not fso.FileExists(engine)    Then missing = missing & "  airdrop\engine.py" & vbCrLf
If Len(missing) > 0 Then
  ts.WriteLine "ABORT: backend missing:" & vbCrLf & missing
  ts.Close
  MsgBox "Backend components missing - the app cannot work:" & vbCrLf & vbCrLf & missing, 16, "DropLink"
  WScript.Quit 1
End If

If Not fso.FolderExists(dataDir & "received") Then fso.CreateFolder dataDir & "received"
If Not fso.FolderExists(dataDir & "keys") Then fso.CreateFolder dataDir & "keys"

Set net = CreateObject("WScript.Network")

WshShell.Environment("PROCESS")("DROPLINK_RADIO_EXE") = radio
WshShell.Environment("PROCESS")("DROPLINK_BRIDGE_EXE") = bridge
WshShell.Environment("PROCESS")("DROPLINK_NETD_EXE") = netd
WshShell.Environment("PROCESS")("DROPLINK_AWDLD_EXE") = awdld
WshShell.Environment("PROCESS")("DROPLINK_PYTHON") = pythonExe
WshShell.Environment("PROCESS")("DROPLINK_ENGINE_SCRIPT") = engine
WshShell.Environment("PROCESS")("DROPLINK_OUTPUT_DIR") = dataDir & "received"
WshShell.Environment("PROCESS")("DROPLINK_KEYS_DIR") = dataDir & "keys"
WshShell.Environment("PROCESS")("DROPLINK_HARDWARE_FILE") = dataDir & "hardware.json"
WshShell.Environment("PROCESS")("DROPLINK_LOG_DIR") = logDir
WshShell.Environment("PROCESS")("DROPLINK_DEVICE_NAME") = net.ComputerName
' Auto-receive is left to data\settings.json: an env var here overrides the
' settings page every launch, so the checkbox never actually turned it off.
If fso.FileExists(setupCmd) Then WshShell.Environment("PROCESS")("DROPLINK_SETUP_CMD") = setupCmd
If fso.FileExists(rollbackCmd) Then WshShell.Environment("PROCESS")("DROPLINK_ROLLBACK_CMD") = rollbackCmd
WshShell.Environment("PROCESS")("DROPLINK_AUTHORIZED") = "1"
WshShell.Environment("PROCESS")("DROPLINK_RECOVERY_VERIFIED") = "1"

ts.WriteLine "exe=" & exe
ts.WriteLine "setup=" & fso.FileExists(setupCmd)
ts.WriteLine "rollback=" & fso.FileExists(rollbackCmd)
ts.WriteLine "launching exe..."
ts.Close

extra = ""
For Each arg In WScript.Arguments
  extra = extra & " " & arg
Next

WshShell.CurrentDirectory = appDir
WshShell.Run """" & exe & """" & extra, 1, False
