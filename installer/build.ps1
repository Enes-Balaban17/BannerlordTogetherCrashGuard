[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseZip)

$ErrorActionPreference = 'Stop'
$archivePath = (Resolve-Path -LiteralPath $ReleaseZip).Path
$repo = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $repo ('.local\installer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $payload | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $module = 'BannerlordTogetherCrashGuard'
    [IO.Compression.ZipFileExtensions]::ExtractToFile(
        $archive.GetEntry("Modules/$module/SubModule.xml"), (Join-Path $payload 'SubModule.xml'))
    [IO.Compression.ZipFileExtensions]::ExtractToFile(
        $archive.GetEntry("Modules/$module/bin/Win64_Shipping_Client/$module.dll"), (Join-Path $payload "$module.dll"))
    [IO.Compression.ZipFileExtensions]::ExtractToFile(
        $archive.GetEntry("Modules/$module/CompanionManagementSuite-LICENSE.txt"), (Join-Path $payload 'CompanionManagementSuite-LICENSE.txt'))
} finally {
    $archive.Dispose()
}

$manifest = ([xml](Get-Content -LiteralPath (Join-Path $payload 'SubModule.xml') -Raw)).Module
$version = $manifest.Version.value
if ($manifest.Id.value -ne $module -or $version -notmatch '^v\d+\.\d+\.\d+$') {
    throw 'Unexpected release module ID or version.'
}

dotnet build (Join-Path $PSScriptRoot 'Installer.csproj') -c Release --source https://api.nuget.org/v3/index.json `
    "-p:PayloadDir=$payload" "-p:ModVersion=$($version.Substring(1))"
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$source = Join-Path $PSScriptRoot "bin\Release\net472\$module-Setup.exe"
$package = Join-Path $repo "dist\$module-$version-Setup.exe"
Copy-Item -LiteralPath $source -Destination $package -Force
Get-FileHash -LiteralPath $package
