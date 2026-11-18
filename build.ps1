[CmdletBinding()]
param(
    [string]$GameDir = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string]$HarmonyBin,
    [string]$TogetherBin,
    [string]$XorberaxBin,
    [switch]$SmokeTest,
    [switch]$CompatibilityTest
)

$ErrorActionPreference = 'Stop'
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$client = 'bin\Win64_Shipping_Client'
$steamApps = Split-Path -Parent (Split-Path -Parent $GameDir)
$workshop = Join-Path $steamApps 'workshop\content\261550'
if (!$HarmonyBin) { $HarmonyBin = Join-Path $workshop "2859188632\$client" }
if (!$TogetherBin) { $TogetherBin = Join-Path $workshop "3761555398\$client" }
$HarmonyBin = (Resolve-Path -LiteralPath $HarmonyBin).Path
$TogetherBin = (Resolve-Path -LiteralPath $TogetherBin).Path

$project = Join-Path $PSScriptRoot 'src\BannerlordTogetherCrashGuard.csproj'
dotnet build $project -c Release --source https://api.nuget.org/v3/index.json `
    "-p:GameDir=$GameDir" "-p:HarmonyBin=$HarmonyBin" "-p:TogetherBin=$TogetherBin"
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$dll = Join-Path $PSScriptRoot 'src\bin\Release\net472\BannerlordTogetherCrashGuard.dll'
if ($SmokeTest) {
    $smokeArgs = @((Join-Path $GameDir $client), (Join-Path $GameDir "Modules\SandBox\$client"),
        $HarmonyBin, $TogetherBin, $dll)
    if ($XorberaxBin) { $smokeArgs += (Resolve-Path -LiteralPath $XorberaxBin).Path }
    powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'tests\smoke.ps1') @smokeArgs
    if ($LASTEXITCODE -ne 0) { throw "Harmony smoke test failed with exit code $LASTEXITCODE" }
}

if ($CompatibilityTest) {
    powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'tests\startup.ps1') $GameDir $dll
    if ($LASTEXITCODE -ne 0) { throw "Startup lifecycle tests failed with exit code $LASTEXITCODE" }
    powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'tests\compatibility.ps1') $GameDir $dll
    if ($LASTEXITCODE -ne 0) { throw "Optional compatibility tests failed with exit code $LASTEXITCODE" }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$module = 'Modules/BannerlordTogetherCrashGuard'
$manifest = Join-Path $PSScriptRoot 'SubModule.xml'
$version = ([xml](Get-Content -LiteralPath $manifest -Raw)).Module.Version.value
$zipPath = Join-Path $dist "BannerlordTogetherCrashGuard-$version.zip"
$stream = [System.IO.File]::Create($zipPath)
try {
    $zip = [System.IO.Compression.ZipArchive]::new(
        $stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $entries = @{
            "$module/SubModule.xml" = $manifest
            "$module/CompanionManagementSuite-LICENSE.txt" = Join-Path $PSScriptRoot 'licenses\CompanionManagementSuite-MIT.txt'
            "$module/bin/Win64_Shipping_Client/BannerlordTogetherCrashGuard.dll" = $dll
            'README.md' = Join-Path $PSScriptRoot 'README.md'
            'CHANGELOG.md' = Join-Path $PSScriptRoot 'CHANGELOG.md'
            'INVESTIGATION.md' = Join-Path $PSScriptRoot 'INVESTIGATION.md'
            'WorkshopCreate.xml' = Join-Path $PSScriptRoot 'WorkshopCreate.xml'
            'WORKSHOP.md' = Join-Path $PSScriptRoot 'WORKSHOP.md'
            'docs/cover.jpg' = Join-Path $PSScriptRoot 'docs\cover.jpg'
        }
        foreach ($entry in $entries.Keys) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $entries[$entry], $entry, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $zip.Dispose()
    }
} finally {
    $stream.Dispose()
}
Get-FileHash -LiteralPath $zipPath
