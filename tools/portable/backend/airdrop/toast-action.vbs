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

Dim uri, here, shell, ps
If WScript.Arguments.Count < 1 Then WScript.Quit 0
uri = WScript.Arguments(0)

Set shell = CreateObject("WScript.Shell")
here = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
ps = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"

' 0 = hidden window, False = do not wait. The worker still does the real work
' (base64url decode, clipboard, explorer), it just cannot be seen starting up.
shell.Run """" & ps & """ -NoProfile -ExecutionPolicy Bypass -File """ & _
          here & "\toast-action.ps1"" """ & uri & """", 0, False
