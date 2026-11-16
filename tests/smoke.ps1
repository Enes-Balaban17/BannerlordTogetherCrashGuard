param($GameBin, $SandboxBin, $HarmonyBin, $TogetherBin, $FixDll)

$ErrorActionPreference = 'Stop'
foreach ($dependency in 'Library','ObjectSystem','DotNet','Engine','Core','CampaignSystem','MountAndBlade') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $GameBin "TaleWorlds.$dependency.dll"))
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $SandboxBin 'SandBox.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $HarmonyBin '0Harmony.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $TogetherBin 'BannerlordTogether.dll'))
$fix = [Reflection.Assembly]::LoadFrom($FixDll)
$owner = 'bannerlord.together.crashguard.smoke'
$harmony = [HarmonyLib.Harmony]::new($owner)
$expected = @(
    'TaleWorlds.CampaignSystem.MapEvents.MapEvent.Update'
    'TaleWorlds.CampaignSystem.MapEvents.MapEvent.FinalizeEventAux'
    'SandBox.SandBoxMissions.OpenBattleMission'
    'TaleWorlds.MountAndBlade.DeploymentMissionController.SetupTeams'
    'TaleWorlds.MountAndBlade.BattleDeploymentMissionController.OnSetupTeamsOfSide'
    'TaleWorlds.MountAndBlade.DeploymentMissionController.FinishDeployment'
    'TaleWorlds.MountAndBlade.Mission.OnTick'
    'TaleWorlds.MountAndBlade.Mission.EndMissionInternal'
    'TaleWorlds.CampaignSystem.AgentOrigins.PartyGroupAgentOrigin.OnAgentRemoved'
)
try {
    $harmony.PatchAll($fix)
    $actual = @($harmony.GetPatchedMethods() | ForEach-Object { "$($_.DeclaringType.FullName).$($_.Name)" })
    if (Compare-Object $expected $actual) { throw "Unexpected patch targets: $($actual -join ', ')" }
    $actual | ForEach-Object { "Patched $_" }
    foreach ($method in 'SetupTeams','FinishDeployment') {
        $target = $harmony.GetPatchedMethods() | Where-Object Name -eq $method
        $guard = [HarmonyLib.Harmony]::GetPatchInfo($target).Finalizers |
            Where-Object { $_.owner -eq $owner -and $_.PatchMethod.ReturnType -eq [Exception] }
        if (!$guard) { throw "Missing exception finalizer on $method" }
    }

    $guardType = $fix.GetType('BannerlordTogetherCrashGuard.DeploymentCrashGuard', $true)
    $binding = [Reflection.BindingFlags]'NonPublic, Static'
    $classify = $guardType.GetMethod('Classify', $binding)
    $cases = @(
        @{ Input = @(0, 0, $false, $true, $false); Expected = 'BROKEN_START_EMPTY_BATTLE' }
        @{ Input = @(1, 0, $true, $true, $false); Expected = 'BROKEN_START_MISSING_SIDE' }
        @{ Input = @(0, 1, $true, $true, $false); Expected = 'BROKEN_START_MISSING_SIDE' }
        @{ Input = @(2, 4, $false, $false, $true); Expected = 'BROKEN_START_NO_PLAYER' }
        @{ Input = @(2, 4, $true, $true, $true); Expected = 'BROKEN_START_DEPLOYMENT_STALLED' }
        @{ Input = @(2, 4, $true, $true, $false); Expected = 'SPAWN_CHECK_PASSED' }
    )
    foreach ($case in $cases) {
        $result = $classify.Invoke($null, [object[]]$case.Input)
        if ($result -ne $case.Expected) { throw "Expected $($case.Expected), got $result" }
    }
    $canSuppress = $guardType.GetMethod('CanSuppress', $binding)
    if (!$canSuppress.Invoke($null, @([NullReferenceException]::new())) -or
        !$canSuppress.Invoke($null, @([InvalidOperationException]::new())) -or
        $canSuppress.Invoke($null, @([AccessViolationException]::new())) -or
        $canSuppress.Invoke($null, @([OutOfMemoryException]::new()))) {
        throw 'Deployment exception policy mismatch'
    }
    'Deployment outcome and exception policy checks passed.'
} finally {
    $harmony.UnpatchAll($owner)
}
if (@($harmony.GetPatchedMethods()).Count -ne 0) { throw 'Smoke patches failed to unload' }
'All nine patch targets installed and unloaded on .NET Framework.'
