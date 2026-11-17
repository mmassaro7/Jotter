@echo off
setlocal
rem Removes Jotter and its Start menu shortcut. Your saved Jotter text and settings
rem (in %LOCALAPPDATA%\Jotter) are kept unless you delete that folder yourself.
set "DEST=%LOCALAPPDATA%\Programs\Jotter"

tasklist /FI "IMAGENAME eq Jotter.exe" | find /I "Jotter.exe" >nul && (
  echo Please close Jotter first, then run this again.
  pause
  exit /b 1
)

del /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Jotter.lnk" 2>nul
del /Q "%DEST%\Jotter.exe" 2>nul
echo Jotter has been uninstalled.
pause
cd /d "%TEMP%"
(goto) 2>nul & rmdir /S /Q "%DEST%"
