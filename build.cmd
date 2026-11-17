@echo off
rem Compila o BatteryTray.exe com o compilador C# do .NET Framework 4 (incluso no Windows).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ /codepage:65001 /win32manifest:BatteryTray.manifest /out:BatteryTray.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll BatteryTray.cs
