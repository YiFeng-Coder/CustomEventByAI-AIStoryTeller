# AI Narrator build script (ASCII only: Windows PowerShell 5.1 mis-parses UTF-8 without BOM).
#
# Why not `dotnet build` / MSBuild: assembling this mod only needs Roslyn, and calling
# csc.dll directly avoids MSBuild's temp/obj machinery entirely. It is also much faster.
#
# IMPORTANT (learned the hard way): the portable SDK shipped inside
# Mods/PioneerFrameworkLite/.zcode/dotnet10 has a host that fails its first-run
# initialisation on this machine (it cannot create its sentinel files under the user
# profile), and every file it then tries to write is denied. The *system* .NET host
# (C:\Program Files\dotnet) works fine, so we prefer the system host + system csc and
# only borrow the net10.0 reference pack from the portable SDK.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/build.ps1
#   powershell -ExecutionPolicy Bypass -File tools/build.ps1 -GameDir "D:\...\RimWorld"
#   powershell -ExecutionPolicy Bypass -File tools/build.ps1 -CheckOnly
param(
    [string]$GameDir = "",
    [string]$RefPackRoot = "",
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

$modRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($GameDir)) {
    if (-not [string]::IsNullOrEmpty($env:RIMWORLD_DIR)) { $GameDir = $env:RIMWORLD_DIR }
    else { $GameDir = Split-Path -Parent (Split-Path -Parent $modRoot) }
}
$asmCSharp = Join-Path $GameDir "Assembly-CSharp.dll"
if (-not (Test-Path $asmCSharp)) { throw "Invalid GameDir: $asmCSharp not found (use -GameDir)" }

function Get-HighestChildDir([string]$root, [string]$filter) {
    if (-not (Test-Path $root)) { return $null }
    return Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like $filter } |
        Sort-Object { try { [version]($_.Name -replace '[^0-9.].*$','') } catch { [version]'0.0' } } -Descending |
        Select-Object -First 1
}

# --- 1. pick host + csc -------------------------------------------------------
$systemDotnet = "C:\Program Files\dotnet\dotnet.exe"
$systemSdks = "C:\Program Files\dotnet\sdk"

$dotnet = $null
$csc = $null

if (Test-Path $systemDotnet) {
    $sdkDir = Get-HighestChildDir $systemSdks "*"
    if ($sdkDir) {
        $candidate = Join-Path $sdkDir.FullName "Roslyn\bincore\csc.dll"
        if (Test-Path $candidate) {
            $dotnet = $systemDotnet
            $csc = $candidate
        }
    }
}

if (-not $csc) {
    # Fall back to any portable SDK next to the mods folder.
    $portableRoots = @(
        (Join-Path $modRoot ".zcode\dotnet10"),
        (Join-Path (Split-Path -Parent $modRoot) "PioneerFrameworkLite\.zcode\dotnet10")
    )
    foreach ($p in $portableRoots) {
        if (-not (Test-Path (Join-Path $p "dotnet.exe"))) { continue }
        $sdkDir = Get-HighestChildDir (Join-Path $p "sdk") "*"
        if (-not $sdkDir) { continue }
        $candidate = Join-Path $sdkDir.FullName "Roslyn\bincore\csc.dll"
        if (Test-Path $candidate) {
            $dotnet = Join-Path $p "dotnet.exe"
            $csc = $candidate
            break
        }
    }
}

if (-not $csc) { throw "No csc.dll found (looked in system SDK and portable SDK)" }

# --- 2. pick a net10.0 reference pack ----------------------------------------
$refRoots = @()
if (-not [string]::IsNullOrEmpty($RefPackRoot)) { $refRoots += $RefPackRoot }
$refRoots += @(
    "C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref",
    (Join-Path (Split-Path -Parent $modRoot) "PioneerFrameworkLite\.zcode\dotnet10\packs\Microsoft.NETCore.App.Ref"),
    (Join-Path $modRoot ".zcode\dotnet10\packs\Microsoft.NETCore.App.Ref")
)

$refDir = $null
foreach ($r in $refRoots) {
    if (-not (Test-Path $r)) { continue }
    $pack = Get-HighestChildDir $r "10.*"
    if (-not $pack) { continue }
    $candidate = Join-Path $pack.FullName "ref\net10.0"
    if (Test-Path $candidate) { $refDir = $candidate; break }
}

if (-not $refDir) { throw "No net10.0 reference assemblies found. Use -RefPackRoot to point at Microsoft.NETCore.App.Ref/10.x" }

# --- 3. collect inputs --------------------------------------------------------
$buildDir = Join-Path $modRoot ".build"
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$outDir = Join-Path $modRoot "1.7\Assemblies"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outDll = Join-Path $outDir "AINarrator.dll"

$srcDir = Join-Path $modRoot "Source\AINarrator"
$sources = Get-ChildItem $srcDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch "\\obj\\|\\bin\\" } |
    ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { throw "No source files found under $srcDir" }

$lines = New-Object System.Collections.Generic.List[string]
foreach ($r in (Get-ChildItem $refDir -Filter *.dll)) { $lines.Add('/r:"' + $r.FullName + '"') }

$gameRefs = @("Assembly-CSharp.dll", "UnityStubs.dll", "Waylith.dll", "System.Text.Json.dll", "System.Net.Http.dll")
foreach ($name in $gameRefs) {
    $full = Join-Path $GameDir $name
    if (Test-Path $full) { $lines.Add('/r:"' + $full + '"') }
    else { Write-Warning "Missing game reference: $full" }
}

$optionalRefs = @(
    (Join-Path $GameDir "Mods\Harmony\Current\Assemblies\0Harmony.dll"),
    (Join-Path $GameDir "Mods\RimLua\1.7\Assemblies\RimLua.dll"),
    (Join-Path $GameDir "Mods\RimLua\1.7\Assemblies\MoonSharp.Interpreter.dll")
)
foreach ($full in $optionalRefs) {
    if (Test-Path $full) { $lines.Add('/r:"' + $full + '"') }
    else { Write-Warning "Missing optional reference: $full" }
}

foreach ($s in $sources) { $lines.Add('"' + $s + '"') }

$rsp = Join-Path $buildDir "ainarrator.rsp"
Set-Content -LiteralPath $rsp -Value $lines -Encoding ASCII

$cscArgs = @(
    "exec", $csc,
    "-noconfig", "-nostdlib+", "-target:library",
    "-langversion:latest", "-nullable:disable",
    "-deterministic", "-debug-",
    "-out:$outDll",
    "@$rsp"
)

Write-Host "host : $dotnet"
Write-Host "csc  : $csc"
Write-Host "ref  : $refDir"
Write-Host "out  : $outDll"

$output = & $dotnet @cscArgs 2>&1
$output | ForEach-Object { Write-Host $_ }
$exit = $LASTEXITCODE

if ($CheckOnly) {
    $errors = @($output | Where-Object { "$_" -match 'error CS\d+' })
    if ($errors.Count -gt 0) {
        Write-Host "COMPILE-CHECK FAILED: $($errors.Count) error(s)"
        exit 1
    }
    Write-Host "COMPILE-CHECK PASSED"
    exit 0
}

if ($exit -ne 0) { throw "csc failed (exit $exit)" }

Write-Host "OK: $outDll ($((Get-Item $outDll).Length) bytes)"
