@echo off
REM Maintainer tool -- double-click to check contests.json Cabrillo names against WA7BNM.
REM Needs Python 3 and an internet connection. Does NOT modify any file.
python "%~dp0verify_contest_cabnames.py" %*
echo.
pause
