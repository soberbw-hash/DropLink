' DropLink - protocol handler for the toast buttons (open receive folder / copy
' file name).
'
' It runs under wscript.exe, and that is the whole point. The handler used to be
' powershell.exe, so every click started a console program with a visible window
' and flashed a black PowerShell box before the folder appeared. Nothing inside
' the script can undo that: the window belongs to the console host, which the
' shell creates before any script code runs. wscript.exe is a GUI-subsystem host,
' so there is no console to flash, and it starts the PowerShell worker with
' window style 0 (hidden) for the same reason.
'
' Usage: wscript //nologo toast-action.vbs "droplink:open?folder=<b64url>"
' Pure ASCII; all real text is UTF-8 base64url inside the URI.
Option Explicit

Dim uri, here, shell, ps, fs, valid, route, hits, python, worker, code
If WScript.Arguments.Count <> 1 Then WScript.Quit 1
uri = WScript.Arguments(0)
Set valid = New RegExp
valid.Pattern = "^droplink:(open|clip)\?[a-zA-Z0-9_=&/?+-]*$"
valid.IgnoreCase = True
If Not valid.Test(uri) Then WScript.Quit 1
Set shell = CreateObject("WScript.Shell")
Set fs = CreateObject("Scripting.FileSystemObject")
here = fs.GetParentFolderName(WScript.ScriptFullName)
Set route = New RegExp
route.Pattern = "^droplink:open\?batch=([a-zA-Z0-9_-]+={0,2})(&|$)"
route.IgnoreCase = True
Set hits = route.Execute(uri)
If hits.Count > 0 Then
    python = fs.BuildPath(fs.GetParentFolderName(here), "python\pythonw.exe")
    worker = fs.BuildPath(here, "open_batch.py")
    If fs.FileExists(python) And fs.FileExists(worker) Then
        code = shell.Run(Quote(python) & " -I -B " & Quote(worker) & " " & Quote(hits(0).SubMatches(0)), 0, True)
        If code = 0 Then WScript.Quit 0
    End If
End If
ps = fs.BuildPath(fs.GetSpecialFolder(0), "System32\WindowsPowerShell\v1.0\powershell.exe")
shell.Run Quote(ps) & " -NoProfile -ExecutionPolicy Bypass -File " & Quote(here & "\toast-action.ps1") & " " & Quote(uri), 0, False
Function Quote(value)
    Quote = Chr(34) & value & Chr(34)
End Function
