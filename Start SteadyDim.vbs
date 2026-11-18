Option Explicit

Dim fso, shell, baseDir, dllPath, dotnetPath

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

baseDir = fso.GetParentFolderName(WScript.ScriptFullName)
dllPath = fso.BuildPath(baseDir, "SteadyDim.dll")

If Not fso.FileExists(dllPath) Then
    MsgBox "SteadyDim.dll was not found next to this launcher.", 16, "SteadyDim"
    WScript.Quit 1
End If

dotnetPath = shell.ExpandEnvironmentStrings("%ProgramFiles%") & "\dotnet\dotnet.exe"

If Not fso.FileExists(dotnetPath) Then
    dotnetPath = shell.ExpandEnvironmentStrings("%ProgramW6432%") & "\dotnet\dotnet.exe"
End If

If Not fso.FileExists(dotnetPath) Then
    dotnetPath = "dotnet.exe"
End If

' Window style 0 hides the dotnet console window; the tray app provides the UI.
shell.Run """" & dotnetPath & """ """ & dllPath & """", 0, False
