#Requires -Version 5.1
<#
.SYNOPSIS
    NINA Startup Service Manager for Windows

.DESCRIPTION
    This script adds or removes N.I.N.A. (Nighttime Imaging 'N' Astronomy) 
    from Windows startup so it launches automatically when you log in.

.PARAMETER Action
    The action to perform: Install, Uninstall, or Status

.PARAMETER NinaPath
    Optional custom path to NINA.exe

.EXAMPLE
    .\NinaStartupService.ps1 -Action Install
    
.EXAMPLE
    .\NinaStartupService.ps1 -Action Install -NinaPath "D:\Apps\NINA\NINA.exe"

.EXAMPLE
    .\NinaStartupService.ps1 -Action Uninstall

.EXAMPLE
    .\NinaStartupService.ps1 -Action Status
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Install", "Uninstall", "Status", "Menu")]
    [string]$Action = "Menu",
    
    [Parameter(Mandatory=$false)]
    [string]$NinaPath
)

$ErrorActionPreference = "Stop"

# Registry path for startup items
$RegPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$RegName = "NINA"

# Default NINA paths to check
$DefaultPaths = @(
    "$env:LOCALAPPDATA\NINA\NINA.exe",
    "$env:ProgramFiles\N.I.N.A\NINA.exe",
    "${env:ProgramFiles(x86)}\N.I.N.A\NINA.exe",
    "$env:LOCALAPPDATA\Programs\NINA\NINA.exe"
)

function Find-Nina {
    param([string]$CustomPath)
    
    if ($CustomPath -and (Test-Path $CustomPath)) {
        return $CustomPath
    }
    
    foreach ($path in $DefaultPaths) {
        if (Test-Path $path) {
            return $path
        }
    }
    
    return $null
}

function Install-NinaStartup {
    param([string]$Path)
    
    $ninaPath = Find-Nina -CustomPath $Path
    
    if (-not $ninaPath) {
        Write-Host ""
        Write-Host "[ERROR] NINA.exe not found!" -ForegroundColor Red
        Write-Host ""
        Write-Host "Searched locations:" -ForegroundColor Yellow
        foreach ($p in $DefaultPaths) {
            Write-Host "  - $p"
        }
        Write-Host ""
        Write-Host "Please specify the path using -NinaPath parameter" -ForegroundColor Yellow
        return $false
    }
    
    try {
        Set-ItemProperty -Path $RegPath -Name $RegName -Value "`"$ninaPath`"" -Type String -Force
        Write-Host ""
        Write-Host "[SUCCESS] NINA added to Windows startup!" -ForegroundColor Green
        Write-Host ""
        Write-Host "Path: $ninaPath" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "NINA will now start automatically when you log in." -ForegroundColor White
        return $true
    }
    catch {
        Write-Host ""
        Write-Host "[ERROR] Failed to add NINA to startup: $_" -ForegroundColor Red
        return $false
    }
}

function Uninstall-NinaStartup {
    try {
        $existing = Get-ItemProperty -Path $RegPath -Name $RegName -ErrorAction SilentlyContinue
        
        if ($existing) {
            Remove-ItemProperty -Path $RegPath -Name $RegName -Force
            Write-Host ""
            Write-Host "[SUCCESS] NINA removed from Windows startup!" -ForegroundColor Green
        }
        else {
            Write-Host ""
            Write-Host "[INFO] NINA was not configured for startup." -ForegroundColor Yellow
        }
        return $true
    }
    catch {
        Write-Host ""
        Write-Host "[ERROR] Failed to remove NINA from startup: $_" -ForegroundColor Red
        return $false
    }
}

function Get-NinaStartupStatus {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "   NINA Startup Status" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    
    try {
        $existing = Get-ItemProperty -Path $RegPath -Name $RegName -ErrorAction SilentlyContinue
        
        if ($existing) {
            Write-Host "[INSTALLED] NINA is configured to start at login." -ForegroundColor Green
            Write-Host ""
            Write-Host "Path: $($existing.NINA)" -ForegroundColor Cyan
            
            # Check if the path is still valid
            $cleanPath = $existing.NINA.Trim('"')
            if (-not (Test-Path $cleanPath)) {
                Write-Host ""
                Write-Host "[WARNING] The configured NINA path no longer exists!" -ForegroundColor Yellow
                Write-Host "Consider reinstalling or updating the path." -ForegroundColor Yellow
            }
        }
        else {
            Write-Host "[NOT INSTALLED] NINA is NOT configured to start at login." -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "[ERROR] Could not check status: $_" -ForegroundColor Red
    }
    
    # Also show detected NINA installation
    Write-Host ""
    $detected = Find-Nina
    if ($detected) {
        Write-Host "Detected NINA installation: $detected" -ForegroundColor Gray
    }
    else {
        Write-Host "No NINA installation detected in default locations." -ForegroundColor Gray
    }
}

function Show-Menu {
    $detected = Find-Nina
    
    while ($true) {
        Clear-Host
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host "   NINA Startup Service Manager" -ForegroundColor Cyan
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host ""
        
        if ($detected) {
            Write-Host "Detected NINA: $detected" -ForegroundColor Green
        }
        else {
            Write-Host "NINA not found in default locations" -ForegroundColor Yellow
        }
        
        Write-Host ""
        Write-Host "  1. Install NINA as Startup Service"
        Write-Host "  2. Remove NINA from Startup"
        Write-Host "  3. Check Current Status"
        Write-Host "  4. Exit"
        Write-Host ""
        
        $choice = Read-Host "  Enter your choice (1-4)"
        
        switch ($choice) {
            "1" {
                if (-not $detected) {
                    Write-Host ""
                    $customPath = Read-Host "Enter full path to NINA.exe"
                    Install-NinaStartup -Path $customPath
                }
                else {
                    Install-NinaStartup -Path $detected
                }
                Write-Host ""
                Read-Host "Press Enter to continue"
            }
            "2" {
                Uninstall-NinaStartup
                Write-Host ""
                Read-Host "Press Enter to continue"
            }
            "3" {
                Get-NinaStartupStatus
                Write-Host ""
                Read-Host "Press Enter to continue"
            }
            "4" {
                Write-Host ""
                Write-Host "Goodbye!" -ForegroundColor Cyan
                return
            }
            default {
                Write-Host "Invalid choice. Please try again." -ForegroundColor Red
                Start-Sleep -Seconds 1
            }
        }
    }
}

# Main execution
switch ($Action) {
    "Install" {
        Install-NinaStartup -Path $NinaPath
    }
    "Uninstall" {
        Uninstall-NinaStartup
    }
    "Status" {
        Get-NinaStartupStatus
    }
    "Menu" {
        Show-Menu
    }
}
