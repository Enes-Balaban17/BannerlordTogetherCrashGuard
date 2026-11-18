param($GameBin, $SandboxBin, $HarmonyBin, $TogetherBin, $FixDll, $XorberaxBin)

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
    'BannerlordTogether.Network.CoopNetworkBase.EnqueueReceivedPacket'
    'BannerlordTogether.Network.CoopNetworkBase.TryDequeueEnvelope'
    'TaleWorlds.CampaignSystem.Campaign.set_SpeedUpMultiplier'
    'TaleWorlds.CampaignSystem.MapEvents.MapEvent.SimulateBattleSessionForMapEvent'
    'TaleWorlds.CampaignSystem.MapEvents.MapEvent.Update'
    'TaleWorlds.CampaignSystem.MapEvents.MapEvent.FinalizeEventAux'
    'SandBox.SandBoxMissions.OpenBattleMission'
    'TaleWorlds.MountAndBlade.DeploymentMissionController.SetupTeams'
    'TaleWorlds.MountAndBlade.DeploymentMissionController.OnMissionTick'
    'TaleWorlds.MountAndBlade.BattleDeploymentMissionController.OnSetupTeamsOfSide'
    'TaleWorlds.MountAndBlade.SiegeDeploymentMissionController.OnSetupTeamsOfSide'
    'TaleWorlds.MountAndBlade.DeploymentMissionController.FinishDeployment'
    'TaleWorlds.MountAndBlade.Mission.OnTick'
    'TaleWorlds.MountAndBlade.Mission.EndMissionInternal'
    'TaleWorlds.CampaignSystem.AgentOrigins.PartyGroupAgentOrigin.OnAgentRemoved'
    'TaleWorlds.CampaignSystem.Party.MobileParty.TeleportPartyToOutSideOfEncounterRadius'
)
try {
    $harmony.PatchAll($fix)
    $actual = @($harmony.GetPatchedMethods() | ForEach-Object { "$($_.DeclaringType.FullName).$($_.Name)" })
    if (Compare-Object $expected $actual) { throw "Unexpected patch targets: $($actual -join ', ')" }
    $actual | ForEach-Object { "Patched $_" }
    $binding = [Reflection.BindingFlags]'NonPublic, Static'
    $teleport = $harmony.GetPatchedMethods() | Where-Object Name -eq 'TeleportPartyToOutSideOfEncounterRadius'
    $captivity = $fix.GetType('BannerlordTogetherCrashGuard.CaptivityReleasePatch', $true)
    $rewrite = $captivity.GetMethod('Transpiler', $binding)
    $original = [HarmonyLib.PatchProcessor]::GetOriginalInstructions($teleport)
    $rewritten = $rewrite.Invoke($null, [object[]]@(,$original))
    $calls = @($rewritten | Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand.Name -eq 'IsNearbyPartyHostile' })
    if ($calls.Count -ne 1 -or
        !([HarmonyLib.Harmony]::GetPatchInfo($teleport).Finalizers | Where-Object owner -eq $owner)) {
        throw 'Captivity relocation rewrite or fallback was not installed'
    }
    'Captivity check replacement and relocation fallback passed.'

    $camera = $fix.GetType('BannerlordTogetherCrashGuard.ShoulderCameraCompatibility', $true)
    $camera.GetMethod('Apply', $binding).Invoke($null, @($harmony))
    if (@($harmony.GetPatchedMethods()).Count -ne $expected.Count) {
        throw 'Camera compatibility should be inert without Xorberax'
    }
    if ($XorberaxBin) {
        $gameRoot = Split-Path -Parent (Split-Path -Parent $GameBin)
        foreach ($dependency in 'ScreenSystem','InputSystem','MountAndBlade.ViewModelCollection') {
            [void][Reflection.Assembly]::LoadFrom((Join-Path $GameBin "TaleWorlds.$dependency.dll"))
        }
        [void][Reflection.Assembly]::LoadFrom((Join-Path $gameRoot 'Modules\Native\bin\Win64_Shipping_Client\TaleWorlds.MountAndBlade.View.dll'))
        $xorberax = [Reflection.Assembly]::LoadFrom((Join-Path $XorberaxBin 'XorberaxLegacy.dll'))
        $target = $camera.GetMethod('FindTarget', $binding).Invoke($null, @($xorberax))
        if (!$target) { throw 'Optional Xorberax target was not found' }
        $camera.GetMethod('Apply', $binding).Invoke($null, @($harmony))
        $camera.GetMethod('Apply', $binding).Invoke($null, @($harmony))
        $prefixes = @([HarmonyLib.Harmony]::GetPatchInfo($target).Prefixes | Where-Object owner -eq $owner)
        if ($prefixes.Count -ne 1) { throw 'Expected exactly one optional Xorberax prefix' }
        "Optional camera hook passed on Xorberax $($xorberax.GetName().Version)."
    }
    foreach ($method in 'SetupTeams','FinishDeployment') {
        $target = $harmony.GetPatchedMethods() | Where-Object Name -eq $method
        $guard = [HarmonyLib.Harmony]::GetPatchInfo($target).Finalizers |
            Where-Object { $_.owner -eq $owner -and $_.PatchMethod.ReturnType -eq [Exception] }
        if (!$guard) { throw "Missing exception finalizer on $method" }
    }

    $guardType = $fix.GetType('BannerlordTogetherCrashGuard.DeploymentCrashGuard', $true)
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
'All patch targets installed and unloaded on .NET Framework.'
