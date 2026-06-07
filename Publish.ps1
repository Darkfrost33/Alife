#Requires -Version 5.1
<#
.SYNOPSIS
    Alife Publish Script - Build and publish all projects
.DESCRIPTION
    Publishes Client, DeskPet, and all Function plugins to the shared distribution directory.
    Copies plugin files and syncs shared NuGet dependencies.
    Uses project defaults from Directory.Build.props (no overrides).
.PARAMETER OutputDir
    Output directory. Defaults to "$PSScriptRoot\..\Shared\Alife\Outputs".
.PARAMETER SyncStorage
    Copy plugin sources to Documents\Alife\Storage\Plugins and PluginsDebug.
.EXAMPLE
    .\Publish.ps1
    .\Publish.ps1 -OutputDir "C:\path\to\dist\Outputs"
    .\Publish.ps1 -OutputDir "D:\AlifeRepo\Outputs" -SyncStorage
#>

param(
    [string]$OutputDir = "",
    [switch]$SyncStorage
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Src = Join-Path $Root "Sources"

if (-not $OutputDir) {
    $OutputDir = Join-Path $Root "..\Shared\Alife\Outputs"
}

# Resolve to absolute path before any operations
$OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
$PluginTarget = Join-Path (Split-Path $OutputDir -Parent) "Plugins"
if (-not $SyncStorage.IsPresent -and $OutputDir.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
    $SyncStorage = $true
}

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "[Alife] Publish Mode"                                  -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "[Alife] Output:      $OutputDir"                       -ForegroundColor Cyan
Write-Host "[Alife] PluginTarget: $PluginTarget"                    -ForegroundColor Cyan
Write-Host ""

# ============================================================
# Step 0: Clean output directory
# ============================================================
Write-Host "[0/4] Cleaning output directory..." -ForegroundColor Yellow

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Write-Host "  Cleaned: $OutputDir" -ForegroundColor Green
Write-Host ""

# ============================================================
# Step 1: Publish applications
# ============================================================
Write-Host "[1/4] Publish applications..." -ForegroundColor Yellow

Write-Host "  Publish Alife.Client..."
dotnet publish (Join-Path $Src "Alife\Alife.Client\Alife.Client.csproj") `
    -c Release -o (Join-Path $OutputDir "Alife.Client") -nologo --verbosity quiet

Write-Host "  Publish Alife.DeskPet.Client..."
dotnet publish (Join-Path $Src "Alife.DeskPet\Alife.DeskPet.Client\Alife.DeskPet.Client.csproj") `
    -c Release -o (Join-Path $OutputDir "Alife.DeskPet.Client") -nologo --verbosity quiet

$functionDirs = Get-ChildItem (Join-Path $Src "Alife.Function") -Directory | Where-Object { $_.Name -match '^Alife\.Function\.' }
foreach ($dir in $functionDirs) {
    $csproj = Join-Path $dir.FullName "$($dir.Name).csproj"
    $funcOut = Join-Path $OutputDir $dir.Name
    Write-Host "  Publish $($dir.Name)..."
    dotnet publish $csproj -c Release -o $funcOut -nologo --verbosity quiet
}

Write-Host ""

# ============================================================
# Step 1b: Copy function assemblies to Client (Release needs these for DI)
# ============================================================
Write-Host "[1b/4] Copying function assemblies to Client..." -ForegroundColor Yellow
$clientDir = Join-Path $OutputDir "Alife.Client"
$baseDir = Join-Path $PluginTarget "BaseDirectory"
New-Item -ItemType Directory -Path $baseDir -Force | Out-Null
foreach ($dir in $functionDirs) {
    $funcOut = Join-Path $OutputDir $dir.Name
    if (-not (Test-Path $funcOut)) { continue }
    Get-ChildItem $funcOut -Filter "Alife*.dll" -File | ForEach-Object {
        Copy-Item $_.FullName $clientDir -Force
        Copy-Item $_.FullName $baseDir -Force
    }
}
Write-Host "  Function assemblies copied to Client and Plugins/BaseDirectory" -ForegroundColor Green
Write-Host ""

# ============================================================
# Step 2: Copy plugins
# ============================================================
Write-Host "[2/4] Copying plugins..." -ForegroundColor Yellow

if (Test-Path $PluginTarget) {
    Remove-Item $PluginTarget -Recurse -Force
}
New-Item -ItemType Directory -Path $PluginTarget -Force | Out-Null

foreach ($dir in $functionDirs) {
    $target = Join-Path $PluginTarget $dir.Name
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    # Copy .cs files from source directory (including subdirectories; skip DeskPet cores shipped in Client DLL)
    $skipCs = if ($dir.Name -eq 'Alife.Function.DeskPet') { @('DeskPetService.cs', 'PetServer.cs') } else { @() }
    Get-ChildItem $dir.FullName -Filter "*.cs" -Recurse -File | Where-Object { $_.FullName -notmatch '\\obj\\' -and $skipCs -notcontains $_.Name } | ForEach-Object {
        $relativePath = $_.FullName.Substring($dir.FullName.Length + 1)
        $destFile = Join-Path $target $relativePath
        $destDir = Split-Path $destFile -Parent
        if (-not (Test-Path $destDir)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }
        Copy-Item $_.FullName $destFile -Force
    }

    # Copy generated Razor .g.cs files (only if corresponding .razor exists)
    $generatedDir = Join-Path $dir.FullName "obj\Release\generated\Microsoft.CodeAnalysis.Razor.Compiler"
    if (Test-Path $generatedDir) {
        Get-ChildItem $generatedDir -Filter "*_razor.g.cs" -Recurse -File | ForEach-Object {
            $razorName = $_.Name -replace '_razor\.g\.cs$', ''
            $razorFile = Join-Path $dir.FullName "$razorName.razor"
            if (Test-Path $razorFile) {
                Copy-Item $_.FullName $target -Force
            } else {
                Write-Host "  [skip] $($_.Name)"
            }
        }
    }

    Write-Host "  [done] $($dir.Name)" -ForegroundColor Green
}

$standalonePluginRoot = Join-Path $Root "Plugins"
if (Test-Path $standalonePluginRoot) {
    foreach ($pluginDir in Get-ChildItem $standalonePluginRoot -Directory) {
        $pluginName = $pluginDir.Name
        $csproj = Join-Path $pluginDir.FullName "$pluginName.csproj"
        if (Test-Path $csproj) {
            Write-Host "  Build $pluginName for razor codegen..."
            dotnet build $csproj -c Release -nologo --verbosity quiet
        }

        $target = Join-Path $PluginTarget $pluginName
        New-Item -ItemType Directory -Path $target -Force | Out-Null

        Get-ChildItem $pluginDir.FullName -Filter "*.cs" -File | ForEach-Object {
            Copy-Item $_.FullName $target -Force
        }

        $generatedDir = Join-Path $pluginDir.FullName "obj\Release\generated\Microsoft.CodeAnalysis.Razor.Compiler"
        if (Test-Path $generatedDir) {
            Get-ChildItem $generatedDir -Filter "*_razor.g.cs" -Recurse -File | ForEach-Object {
                $razorName = $_.Name -replace '_razor\.g\.cs$', ''
                $razorFile = Join-Path $pluginDir.FullName "$razorName.razor"
                if (Test-Path $razorFile) {
                    Copy-Item $_.FullName $target -Force
                } else {
                    Write-Host "  [skip] $($_.Name)"
                }
            }
        }

        $deskPetDll = Join-Path $OutputDir "Alife.Function.DeskPet\Alife.Function.DeskPet.dll"
        $protocolDll = Join-Path $OutputDir "Alife.Function.DeskPet\Alife.DeskPet.Protocol.dll"
        if (Test-Path $deskPetDll) {
            Copy-Item $deskPetDll $target -Force
        }
        if (Test-Path $protocolDll) {
            Copy-Item $protocolDll $target -Force
        }

        Write-Host "  [done] $pluginName" -ForegroundColor Green
    }
}
Write-Host ""

# ============================================================
# Step 3: Sync plugins to runtime storage (local dev)
# ============================================================
if ($SyncStorage) {
    Write-Host "[4/4] Syncing plugins to runtime storage..." -ForegroundColor Yellow
    $storageRoot = Join-Path $env:USERPROFILE "Documents\Alife\Storage"
    $officialPluginNames = $functionDirs | ForEach-Object { $_.Name }

    foreach ($subdir in @("Plugins", "PluginsDebug")) {
        $destRoot = Join-Path $storageRoot $subdir
        New-Item -ItemType Directory -Path $destRoot -Force | Out-Null
        $pluginDirs = Get-ChildItem $PluginTarget -Directory
        if ($subdir -eq "PluginsDebug") {
            # Debug Client already includes official Function DLLs; avoid duplicate .cs hot-compile.
            $pluginDirs = $pluginDirs | Where-Object { $officialPluginNames -notcontains $_.Name -and $_.Name -ne "BaseDirectory" }
        }
        $pluginDirs | ForEach-Object {
            $dest = Join-Path $destRoot $_.Name
            New-Item -ItemType Directory -Path $dest -Force | Out-Null
            Get-ChildItem $_.FullName -File | ForEach-Object {
                if ($subdir -eq "PluginsDebug" -and $_.Name -match '^Alife\.(Function\.DeskPet|DeskPet\.Protocol)\.dll$') {
                    return
                }
                Copy-Item $_.FullName $dest -Force
            }
        }
        Write-Host "  Synced to $destRoot" -ForegroundColor Green
    }
    Write-Host ""
} else {
    Write-Host "[4/4] Skipped runtime storage sync (use -SyncStorage to enable)" -ForegroundColor DarkGray
    Write-Host ""
}

Write-Host ""
Write-Host "===================================================" -ForegroundColor Green
Write-Host "[Success] Publish complete!"                           -ForegroundColor Green
Write-Host "  Plugins: $PluginTarget"                               -ForegroundColor Green
Write-Host "===================================================" -ForegroundColor Green

# Write-Host ""
# Write-Host "Press any key to exit..."
# $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
