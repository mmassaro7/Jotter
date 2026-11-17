@echo off
setlocal
rem Installs Jotter for the current user (no admin rights needed).
set "DEST=%LOCALAPPDATA%\Programs\Jotter"

if not exist "%~dp0Jotter.exe" (
  echo Jotter.exe was not found next to this file. Unzip the whole download first.
  pause
  exit /b 1
)

tasklist /FI "IMAGENAME eq Jotter.exe" | find /I "Jotter.exe" >nul && (
  echo Please close Jotter first, then run this installer again.
  pause
  exit /b 1
)

if not exist "%DEST%" mkdir "%DEST%"
copy /Y "%~dp0Jotter.exe" "%DEST%\Jotter.exe" >nul || (
  echo Could not copy Jotter.exe to %DEST%.
  pause
  exit /b 1
)
copy /Y "%~dp0Uninstall.cmd" "%DEST%\Uninstall.cmd" >nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs') + '\Jotter.lnk');" ^
  "$s.TargetPath = '%DEST%\Jotter.exe'; $s.WorkingDirectory = '%DEST%'; $s.Description = 'Jotter text editor'; $s.Save()"

echo.
echo Jotter is installed. Open Start and type "Jotter" to launch it.
pause
