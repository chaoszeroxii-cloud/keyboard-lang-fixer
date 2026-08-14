' Starts KeyboardLangFixer with no console window at all.
' Double-click this file, or let Install-Startup.ps1 run it at login.
Dim shell, fso, here, ps1
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
here = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = here & "\KeyboardLangFixer.ps1"

If Not fso.FileExists(ps1) Then
    MsgBox "KeyboardLangFixer.ps1 not found next to this launcher:" & vbCrLf & ps1, 16, "Keyboard Language Fixer"
    WScript.Quit 1
End If

' 0 = hidden window, False = do not wait for it to finish
shell.Run "powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File """ & ps1 & """ -Relaunched", 0, False
