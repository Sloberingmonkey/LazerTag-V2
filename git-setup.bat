@echo off
REM Run this once from inside the Server\ folder to initialise git.
REM After this, use VS Code's built-in Source Control panel (Ctrl+Shift+G) for all commits.

echo === LazerTag Git Setup ===
echo.

REM Check git is installed
where git >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: git is not installed.
    echo Download it from https://git-scm.com/download/win then re-run this script.
    pause
    exit /b 1
)

REM Initialise the repo if not already done
if not exist ".git" (
    git init
    echo Git repository initialised.
) else (
    echo Git repository already exists.
)

REM Set up .gitignore (already in the folder)
if exist ".gitignore" (
    echo .gitignore found.
) else (
    echo WARNING: .gitignore not found. Build output may get committed.
)

REM Stage everything and make the first commit
git add .
git status

echo.
echo Ready to commit. Type a message and press Enter:
set /p MSG=Commit message: 

git commit -m "%MSG%"

echo.
echo === Done ===
echo.
echo Your code is now tracked by git.
echo Use VS Code Source Control (Ctrl+Shift+G) to commit future changes.
echo.
echo OPTIONAL: To back up to GitHub (private repo recommended):
echo   1. Create a new private repo at https://github.com/new
echo   2. Run: git remote add origin https://github.com/YOUR_USERNAME/lazertag.git
echo   3. Run: git push -u origin main
echo.
pause
