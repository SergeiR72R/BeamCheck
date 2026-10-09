<#
  Installs BeamCheck for the current user (no admin rights needed). Start via install.cmd.
    - AutoCAD-based PERI CAD 24:
        * copies BeamCheck.bundle to %APPDATA%\Autodesk\ApplicationPlugins (autoloader), and
        * registers a demand-load key (load on startup) in every AutoCAD profile found under
          HKCU\Software\Autodesk\AutoCAD\R<ver>\<product>\Applications — works even when the
          autoloader is switched off (APPAUTOLOAD).
    - BricsCAD-based PERI CAD 25/26: copies the bundle to %APPDATA%\BeamCheck and registers
      load-on-startup keys for every BricsCAD profile (V25 → net48, V26+ → net8).
  A log is written to %APPDATA%\BeamCheck\install.log.
#>
$ErrorActionPreference = "Stop"
$logDir = Join-Path $env:APPDATA "BeamCheck"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Start-Transcript -Path (Join-Path $logDir "install.log") -Force | Out-Null
try {
    $bundle = Join-Path $PSScriptRoot "BeamCheck.bundle"
    if (-not (Test-Path $bundle)) { throw "Папка BeamCheck.bundle не найдена рядом с install.ps1 ($PSScriptRoot). Распакуйте архив целиком." }
    Get-ChildItem $bundle -Recurse -File | Unblock-File

    # --- AutoCAD-based PERI CAD ---
    if (Test-Path (Join-Path $bundle "Contents\Acad")) {
        $acadPlugins = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"
        New-Item -ItemType Directory -Force -Path $acadPlugins | Out-Null
        $target = Join-Path $acadPlugins "BeamCheck.bundle"
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Copy-Item $bundle $target -Recurse
        Get-ChildItem $target -Recurse -File | Unblock-File
        Write-Host "AutoCAD: пакет скопирован в $target"

        $base = "HKCU:\Software\Autodesk\AutoCAD"
        $registered = 0
        if (Test-Path $base) {
            foreach ($rel in Get-ChildItem $base) {
                if ($rel.PSChildName -notmatch '^R(\d+)\.(\d+)$') { continue }
                $major = [int]$Matches[1]
                $dir = if ($major -ge 25) { "net8" } else { "net48" }
                $dll = Join-Path $target "Contents\Acad\$dir\BeamCheck.Acad.dll"
                if (-not (Test-Path $dll)) { Write-Warning "$($rel.PSChildName): нет $dll"; continue }
                foreach ($product in Get-ChildItem $rel.PSPath) {
                    if ($product.PSChildName -notmatch '^ACAD-') { continue }
                    $key = Join-Path $product.PSPath "Applications\BeamCheck"
                    New-Item -Path $key -Force | Out-Null
                    New-ItemProperty -Path $key -Name "DESCRIPTION" -Value "BeamCheck" -PropertyType String -Force | Out-Null
                    New-ItemProperty -Path $key -Name "LOADCTRLS" -Value 2 -PropertyType DWord -Force | Out-Null
                    New-ItemProperty -Path $key -Name "LOADER" -Value $dll -PropertyType String -Force | Out-Null
                    New-ItemProperty -Path $key -Name "MANAGED" -Value 1 -PropertyType DWord -Force | Out-Null
                    Write-Host "AutoCAD $($rel.PSChildName)\$($product.PSChildName): автозагрузка $dll"
                    $registered++
                }
            }
        }
        if ($registered -eq 0) { Write-Warning "Профили AutoCAD в реестре не найдены: модуль загрузится только через автозагрузчик bundle или NETLOAD." }
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
                    Write-Host "BricsCAD $($ver.PSChildName)\$($profile.PSChildName): автозагрузка $dll"
                }
            }
        }
    }

    Write-Host ""
    Write-Host "Готово. Перезапустите PERI CAD: в командной строке должно появиться 'BeamCheck загружен'." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "ОШИБКА: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Stop-Transcript | Out-Null
}
