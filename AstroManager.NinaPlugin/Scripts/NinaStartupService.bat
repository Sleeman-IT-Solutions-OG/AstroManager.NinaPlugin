@echo off
setlocal enabledelayedexpansion

:: =============================================================================
:: NINA Startup Service Installer/Uninstaller for Windows
:: This script adds or removes N.I.N.A. from Windows startup
:: =============================================================================

title NINA Startup Service Manager

:: Check for admin rights
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo.
    echo ========================================
    echo   Administrator privileges required!
    echo ========================================
    echo.
    echo Please right-click this script and select
    echo "Run as administrator"
    echo.
    pause
    exit /b 1
)

:: Default NINA installation paths to check
set "NINA_PATH_1=%LOCALAPPDATA%\NINA\NINA.exe"
set "NINA_PATH_2=%ProgramFiles%\N.I.N.A\NINA.exe"
set "NINA_PATH_3=%ProgramFiles(x86)%\N.I.N.A\NINA.exe"
set "NINA_PATH="

:: Try to find NINA
if exist "%NINA_PATH_1%" set "NINA_PATH=%NINA_PATH_1%"
if exist "%NINA_PATH_2%" set "NINA_PATH=%NINA_PATH_2%"
if exist "%NINA_PATH_3%" set "NINA_PATH=%NINA_PATH_3%"

:menu
cls
echo.
echo ========================================
echo   NINA Startup Service Manager
echo ========================================
echo.
if defined NINA_PATH (
    echo   Detected NINA: %NINA_PATH%
) else (
    echo   NINA not found in default locations
)
echo.
echo   1. Install NINA as Startup Service
echo   2. Remove NINA from Startup
echo   3. Set Custom NINA Path
echo   4. Check Current Status
echo   5. Exit
echo.
set /p choice="  Enter your choice (1-5): "

if "%choice%"=="1" goto install
if "%choice%"=="2" goto uninstall
if "%choice%"=="3" goto custom_path
if "%choice%"=="4" goto status
if "%choice%"=="5" goto end
goto menu

:custom_path
echo.
set /p "NINA_PATH=Enter full path to NINA.exe: "
if not exist "%NINA_PATH%" (
    echo.
    echo ERROR: File not found at specified path!
    pause
    goto menu
)
echo.
echo NINA path set to: %NINA_PATH%
pause
goto menu

:install
if not defined NINA_PATH (
    echo.
    echo ERROR: NINA path not set! Please use option 3 to set the path.
    pause
    goto menu
)
if not exist "%NINA_PATH%" (
    echo.
    echo ERROR: NINA.exe not found at: %NINA_PATH%
    echo Please use option 3 to set the correct path.
    pause
    goto menu
)

echo.
echo Installing NINA as startup service...
echo.

:: Method 1: Add to Registry (Current User - doesn't need admin for this part)
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "NINA" /t REG_SZ /d "\"%NINA_PATH%\"" /f >nul 2>&1

if %errorLevel% equ 0 (
    echo [SUCCESS] NINA added to Windows startup!
    echo.
    echo NINA will now start automatically when you log in.
    echo.
    echo Path: %NINA_PATH%
) else (
    echo [ERROR] Failed to add NINA to startup.
    echo Please try running as administrator.
)
echo.
pause
goto menu

:uninstall
echo.
echo Removing NINA from startup...
echo.

:: Remove from Registry
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "NINA" /f >nul 2>&1

if %errorLevel% equ 0 (
    echo [SUCCESS] NINA removed from Windows startup!
) else (
    echo [INFO] NINA was not found in startup, or already removed.
)
echo.
pause
goto menu

:status
echo.
echo ========================================
echo   Current Startup Status
echo ========================================
echo.

:: Check Registry
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "NINA" >nul 2>&1
if %errorLevel% equ 0 (
    echo [INSTALLED] NINA is configured to start at login.
    echo.
    for /f "tokens=2*" %%a in ('reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "NINA" 2^>nul ^| findstr NINA') do (
        echo Path: %%b
    )
) else (
    echo [NOT INSTALLED] NINA is NOT configured to start at login.
)
echo.
pause
goto menu

:end
echo.
echo Goodbye!
exit /b 0
