param([Parameter(Mandatory)][string]$SetupExe)
$ErrorActionPreference = 'Stop'
$setup = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $SetupExe).Path)
$program = $setup.GetType('BannerlordTogetherCrashGuard.Setup.Program', $true)
$flags = [Reflection.BindingFlags]'Static, NonPublic'
$install = $program.GetMethod('Install', $flags)
$unblock = $program.GetMethod('UnblockFile', $flags)
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repoRoot ('.local\installer-test-' + [Guid]::NewGuid().ToString('N'))
$gameBin = Join-Path $fixture 'bin\Win64_Shipping_Client'
$native = Join-Path $fixture 'Modules\Native'
New-Item -ItemType Directory -Path $gameBin,$native -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $gameBin 'Bannerlord.exe'), [byte[]]@())
[IO.File]::WriteAllText((Join-Path $native 'SubModule.xml'), '<Module/>')
$install.Invoke($null, [object[]]@([string]$fixture))
$module = Join-Path $fixture 'Modules\BannerlordTogetherCrashGuard'
$dll = Join-Path $module 'bin\Win64_Shipping_Client\BannerlordTogetherCrashGuard.dll'
$version = ([xml](Get-Content -LiteralPath (Join-Path $module 'SubModule.xml') -Raw)).Module.Version.value
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString(3)
if ($version -ne ('v' + $assemblyVersion)) { throw 'Installed manifest and assembly versions differ' }
$before = (Get-FileHash -LiteralPath $dll).Hash
foreach ($resourceName in 'SubModule.xml','BannerlordTogetherCrashGuard.dll') {
    $stream = $setup.GetManifestResourceStream($resourceName)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        $embeddedHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')
        $path = if ($resourceName.EndsWith('.dll')) { $dll } else { Join-Path $module $resourceName }
        if ((Get-FileHash -LiteralPath $path).Hash -ne $embeddedHash) { throw 'Installed payload differs from embedded resource' }
    } finally { $stream.Dispose() }
}
Set-Content -LiteralPath $dll -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3`r`n"
if (!(Get-Item -LiteralPath $dll -Stream Zone.Identifier -ErrorAction SilentlyContinue)) { throw 'Download-zone fixture missing' }
$unblock.Invoke($null, [object[]]@([string]$dll))
if (Get-Item -LiteralPath $dll -Stream Zone.Identifier -ErrorAction SilentlyContinue) { throw 'Download zone was not removed' }
if ((Get-FileHash -LiteralPath $dll).Hash -ne $before) { throw 'Unblocking changed DLL contents' }
$unblock.Invoke($null, [object[]]@([string]$dll))
$sentinel = Join-Path $module 'previous-installation.txt'
[IO.File]::WriteAllText($sentinel, 'preserve existing installation')
$install.Invoke($null, [object[]]@([string]$fixture))
$backups = @(Get-ChildItem -LiteralPath $fixture -Directory -Force -Filter '.BannerlordTogetherCrashGuard-*' |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'previous\previous-installation.txt') })
if ($backups.Count -ne 1) { throw 'Previous installation backup was not retained' }
if ((Get-FileHash -LiteralPath $dll).Hash -ne $before) { throw 'Reinstallation changed the payload' }
'Installer checks passed: embedded payload, automatic unblock, absent-zone retry and retained upgrade backup. No game launched.'
