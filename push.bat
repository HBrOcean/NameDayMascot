@echo off
setlocal
set "REPO=https://github.com/HBrOcean/NameDayMascot.git"
set "BRANCH=main"

echo ================================================
echo   NameDayMascot  -^>  GitHub push
echo ================================================
echo.

where git >nul 2>&1
if errorlevel 1 goto NOGIT

git config user.name >nul 2>&1
if errorlevel 1 goto NONAME

git config user.email >nul 2>&1
if errorlevel 1 goto NOEMAIL

if not exist ".git" (
    echo [1/5] git init
    git init
    if errorlevel 1 goto FAIL
    git symbolic-ref HEAD refs/heads/%BRANCH%
) else (
    echo [1/5] repo already initialized
)

echo [2/5] remote
git remote get-url origin >nul 2>&1
if errorlevel 1 (
    git remote add origin %REPO%
) else (
    git remote set-url origin %REPO%
)
if errorlevel 1 goto FAIL

echo [3/5] staging files
git add -A
if errorlevel 1 goto FAIL

echo [4/5] commit
git commit -m "Update NameDayMascot"
if errorlevel 1 echo         nothing to commit - skipped

echo [5/5] push
git push -u origin %BRANCH%
if errorlevel 1 goto PUSHFAIL

echo.
echo SUCCESS
echo   https://github.com/HBrOcean/NameDayMascot
goto END

:NOGIT
echo [ERROR] git not found.
echo   Install Git for Windows: https://git-scm.com/download/win
echo   Then close and reopen this window so PATH refreshes.
goto END

:NONAME
echo [ERROR] git user.name is not set.
echo   Run these two lines once, then re-run this file:
echo     git config --global user.name "HBrOcean"
echo     git config --global user.email "your-email@example.com"
goto END

:NOEMAIL
echo [ERROR] git user.email is not set.
echo   Run these two lines once, then re-run this file:
echo     git config --global user.name "HBrOcean"
echo     git config --global user.email "your-email@example.com"
goto END

:PUSHFAIL
echo.
echo [ERROR] push failed. Check these in order:
echo   1. Repo exists? Create it first at https://github.com/new
echo        name: NameDayMascot     visibility: Public     do NOT add README
echo   2. Wrong branch? This script pushes: %BRANCH%
echo   3. Auth: a browser window may pop up asking you to sign in to GitHub.
echo   4. Network: set HTTPS_PROXY if you are behind a proxy.
goto END

:FAIL
echo.
echo [ERROR] a git command failed. Read the message above it.

:END
echo.
pause
endlocal
