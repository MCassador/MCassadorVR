@echo off
rem Compila o MCassador VR (.NET Framework 4.8, que ja vem no Windows 10/11) com o compilador C# do Visual Studio 2022 / Build Tools.
setlocal
set "CSC=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if not exist "%CSC%" for /d %%e in ("%ProgramFiles%\Microsoft Visual Studio\2022\*") do if exist "%%e\MSBuild\Current\Bin\Roslyn\csc.exe" set "CSC=%%e\MSBuild\Current\Bin\Roslyn\csc.exe"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set REFS=-r:"%FW%\mscorlib.dll" -r:"%FW%\System.dll" -r:"%FW%\System.Core.dll" -r:"%FW%\System.Drawing.dll" -r:"%FW%\System.Windows.Forms.dll" -r:"%FW%\System.Net.Http.dll" -r:"%FW%\System.IO.Compression.dll" -r:"%FW%\System.IO.Compression.FileSystem.dll" -r:"%FW%\System.Web.Extensions.dll"
"%CSC%" -nologo -nostdlib+ -noconfig -optimize+ -target:winexe -out:"MCassador VR.exe" -win32icon:app.ico -win32manifest:app.manifest %REFS% Program.cs
