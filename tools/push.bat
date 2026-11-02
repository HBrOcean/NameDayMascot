@echo off
setlocal
rem This file lives in tools\ ; switch to the project root first,
rem otherwise "git init" would create a bogus repo inside tools\.
cd /d "%~dp0.."
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
    echo [1/6] git init
    git init
    if errorlevel 1 goto FAIL
    git symbolic-ref HEAD refs/heads/%BRANCH%
) else (
    echo [1/6] repo already initialized
)

echo [2/6] remote
git remote get-url origin >nul 2>&1
if errorlevel 1 (
    git remote add origin %REPO%
) else (
    git remote set-url origin %REPO%
)
if errorlevel 1 goto FAIL

echo [3/6] staging files
git add -A
if errorlevel 1 goto FAIL

echo [4/6] commit
git commit -m "Update NameDayMascot"
if errorlevel 1 echo         nothing to commit - skipped

echo [5/6] sync with remote
git fetch origin
if errorlevel 1 goto PUSHFAIL

git rev-parse --verify origin/%BRANCH% >nul 2>&1
if errorlevel 1 (
    echo         remote branch not found - first push
    goto DOPUSH
)

git merge-base --is-ancestor origin/%BRANCH% HEAD >nul 2>&1
if not errorlevel 1 (
    echo         up to date with remote
    goto DOPUSH
)

echo         diverged from remote
echo         adopting remote base, keeping all local files
git reset --soft origin/%BRANCH%
if errorlevel 1 goto FAIL

git add -A
if errorlevel 1 goto FAIL

git commit -m "Update NameDayMascot"
if errorlevel 1 echo         nothing new to commit

:DOPUSH
echo [6/6] push
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
echo   2. Auth: a browser window may pop up asking you to sign in to GitHub.
echo   3. Network: set HTTPS_PROXY if you are behind a proxy.
echo   4. If it says "diverged" again, run git status and send the output.
goto END

:FAIL
echo.
echo [ERROR] a git command failed. Read the message above it.

:END
echo.
pause
endlocal
