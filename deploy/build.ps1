<#
  Builds BeamCheck and assembles dist\BeamCheck.bundle:
    Contents\Acad\net48  — PERI CAD 24 / AutoCAD 2021-2024 (.NET Framework 4.8)
    Contents\Acad\net8   — AutoCAD 2025+ (.NET 8)
    Contents\Bcad\net48  — PERI CAD on BricsCAD V25 (.NET Framework 4.8)
    Contents\Bcad\net8   — PERI CAD on BricsCAD V26+ (.NET 8)
  BricsCAD builds are skipped when -BricsCADDir is not given / not found.

  .\deploy\build.ps1 -BricsCADDir "C:\Program Files\Bricsys\BricsCAD V26 en_US"
#>
param(
    [string]$BricsCADDir = "",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist\BeamCheck.bundle"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $PSScriptRoot "BeamCheck.bundle\PackageContents.xml") $dist

dotnet test (Join-Path $root "tests\BeamCheck.Core.Tests") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

dotnet build (Join-Path $root "src\BeamCheck.Acad") -c $Configuration -o (Join-Path $dist "Contents\Acad\net48")
if ($LASTEXITCODE -ne 0) { throw "AutoCAD (net48) build failed" }
dotnet build (Join-Path $root "src\BeamCheck.Acad") -c $Configuration -p:AcadTargetFramework=net8.0-windows -p:AutoCADNetVersion=25.1.0 -p:BaseIntermediateOutputPath=obj-net8\ -o (Join-Path $dist "Contents\Acad\net8")
if ($LASTEXITCODE -ne 0) { throw "AutoCAD (net8) build failed" }

if ($BricsCADDir -and (Test-Path (Join-Path $BricsCADDir "BrxMgd.dll"))) {
    foreach ($tf in @(@{ tf = "net48"; dir = "net48" }, @{ tf = "net8.0-windows"; dir = "net8" })) {
        dotnet build (Join-Path $root "src\BeamCheck.Bcad") -c $Configuration -f $tf.tf -p:BricsCADDir="$BricsCADDir" -o (Join-Path $dist ("Contents\Bcad\" + $tf.dir))
        if ($LASTEXITCODE -ne 0) { throw "BricsCAD build ($($tf.tf)) failed" }
    }
} else {
    Write-Warning "BricsCAD build skipped (pass -BricsCADDir with BrxMgd.dll)."
}

Copy-Item (Join-Path $PSScriptRoot "install.ps1") (Split-Path $dist -Parent)
Write-Host "Done: $dist"
