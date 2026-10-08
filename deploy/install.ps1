<#
  Installs BeamCheck for the current user (no admin rights needed).
    - PERI CAD 24 / AutoCAD: copies BeamCheck.bundle to %APPDATA%\Autodesk\ApplicationPlugins (autoload).
    - PERI CAD on BricsCAD: copies the bundle to %APPDATA%\BeamCheck and registers demand-load keys
      for every BricsCAD profile found under HKCU\Software\Bricsys\BricsCAD (V25 → net48, V26+ → net8).
  Run from the folder that contains BeamCheck.bundle:  powershell -ExecutionPolicy Bypass -File install.ps1
#>
$ErrorActionPreference = "Stop"
$bundle = Join-Path $PSScriptRoot "BeamCheck.bundle"
if (-not (Test-Path $bundle)) { throw "BeamCheck.bundle not found next to install.ps1" }
Get-ChildItem $bundle -Recurse -File | Unblock-File

# --- AutoCAD-based PERI CAD 24 ---
$acadPlugins = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"
if (Test-Path (Join-Path $bundle "Contents\Acad")) {
    New-Item -ItemType Directory -Force -Path $acadPlugins | Out-Null
    $target = Join-Path $acadPlugins "BeamCheck.bundle"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $bundle $target -Recurse
    Write-Host "AutoCAD / PERI CAD 24: installed to $target"
}

# --- BricsCAD-based PERI CAD 25/26 ---
if (Test-Path (Join-Path $bundle "Contents\Bcad")) {
    $bcHome = Join-Path $env:APPDATA "BeamCheck\BeamCheck.bundle"
    if (Test-Path $bcHome) { Remove-Item $bcHome -Recurse -Force }
    Copy-Item $bundle $bcHome -Recurse

    $base = "HKCU:\Software\Bricsys\BricsCAD"
    if (Test-Path $base) {
        foreach ($ver in Get-ChildItem $base) {
            $major = 0
            if ($ver.PSChildName -match '^V(\d+)') { $major = [int]$Matches[1] }
            $dir = if ($major -ge 26) { "net8" } else { "net48" }
            $dll = Join-Path $bcHome "Contents\Bcad\$dir\BeamCheck.Bcad.dll"
            if (-not (Test-Path $dll)) { continue }
            foreach ($profile in Get-ChildItem $ver.PSPath) {
                $key = Join-Path $profile.PSPath "Applications\BeamCheck"
                New-Item -Path $key -Force | Out-Null
                New-ItemProperty -Path $key -Name "LOADER" -Value $dll -PropertyType String -Force | Out-Null
                New-ItemProperty -Path $key -Name "LOADCTRLS" -Value 2 -PropertyType DWord -Force | Out-Null
                New-ItemProperty -Path $key -Name "MANAGED" -Value 1 -PropertyType DWord -Force | Out-Null
                New-ItemProperty -Path $key -Name "DESCRIPTION" -Value "BeamCheck" -PropertyType String -Force | Out-Null
                Write-Host "BricsCAD $($ver.PSChildName)\$($profile.PSChildName): autoload $dll"
            }
        }
    } else {
        Write-Warning "BricsCAD registry profile not found. Load manually with NETLOAD: $bcHome\Contents\Bcad\<net48|net8>\BeamCheck.Bcad.dll"
    }
}
Write-Host "Restart PERI CAD and run BEAMLOAD."
