Option Explicit

Dim fso, shell, baseDir, dllPath, dotnetPath, appArgs, isRestart

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

isRestart = WScript.Arguments.Named.Exists("restart")

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

appArgs = """" & dllPath & """"

If isRestart Then
    WScript.Sleep 2500
    appArgs = appArgs & " --restart"
End If

' Window style 0 hides the dotnet console window; the tray app provides the UI.
shell.Run """" & dotnetPath & """ " & appArgs, 0, False
