@echo off
rem Builds and runs SnipTest.exe, the self-tests. Exit code 0 = pass.
rem Needs the Windows SDK only to build (for Windows.winmd).
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
for /d %%d in ("%ProgramFiles(x86)%\Windows Kits\10\UnionMetadata\10.*") do set WINMD=%%d\Windows.winmd
if not exist "%WINMD%" (echo Windows SDK not found: install it to build. & exit /b 1)
cd /d "%~dp0"
"%FW%\csc.exe" /nologo /codepage:65001 /target:exe /main:SnipTest /out:SnipTest.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  /r:"%WINMD%" ^
  Snip.cs Editor.cs SnipTest.cs
if errorlevel 1 exit /b 1
.\SnipTest.exe
