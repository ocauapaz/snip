@echo off
rem Builds Snip.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem Needs the Windows SDK only to build (for Windows.winmd); the exe runs without it.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
for /d %%d in ("%ProgramFiles(x86)%\Windows Kits\10\UnionMetadata\10.*") do set WINMD=%%d\Windows.winmd
if not exist "%WINMD%" (echo Windows SDK not found: install it to build. & exit /b 1)
cd /d "%~dp0"
set REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%WINMD%"

rem The exe icon comes from the same drawing code as the tray icon.
"%FW%\csc.exe" /nologo /codepage:65001 /target:exe /main:IconGen /out:IconGen.exe %REFS% Snip.cs Editor.cs IconGen.cs
if errorlevel 1 exit /b 1
.\IconGen.exe snip.ico
if errorlevel 1 exit /b 1
del IconGen.exe

"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /out:Snip.exe /win32icon:snip.ico %REFS% Snip.cs Editor.cs
