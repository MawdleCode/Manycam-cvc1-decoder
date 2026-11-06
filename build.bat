@echo off
rem Builds cvc1conv.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe from .NET Framework 4.x
  exit /b 1
)
"%CSC%" /nologo /optimize+ /target:exe /platform:anycpu /out:"%~dp0cvc1conv.exe" "%~dp0src\*.cs"
if errorlevel 1 exit /b 1
echo Built %~dp0cvc1conv.exe
