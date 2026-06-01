Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

scriptsDir = fso.GetParentFolderName(WScript.ScriptFullName)
root = fso.GetParentFolderName(scriptsDir)

Set env = shell.Environment("PROCESS")
env("DOTNET_CLI_HOME") = root & "\.dotnet-home"
env("APPDATA") = root & "\.appdata"
env("LOCALAPPDATA") = root & "\.localappdata"

publishedExe = root & "\StarBridge.Desktop\bin\Release\net8.0-windows\win-x64\publish\Star Bridge.exe"

If fso.FileExists(publishedExe) Then
    command = """" & publishedExe & """"
Else
    command = "dotnet run --project """ & root & "\StarBridge.Desktop\StarBridge.Desktop.csproj" & """"
End If

shell.CurrentDirectory = root
shell.Run command, 0, False
