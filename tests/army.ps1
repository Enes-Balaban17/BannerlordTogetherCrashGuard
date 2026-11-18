# Sourced by compatibility.ps1 with the real game/mod assemblies and $fix already loaded.
$armyFlags = [Reflection.BindingFlags]'Static, NonPublic'
$armyMenu = $fix.GetType('BannerlordTogetherCrashGuard.ClanArmyCompatibility', $true)
$chord = $armyMenu.GetMethod('ShortcutPressed', $armyFlags)
[void]$chord.Invoke($null, @($false))
if (!$chord.Invoke($null, @($true)) -or $chord.Invoke($null, @($true)) -or $chord.Invoke($null, @($true))) { throw 'Army shortcut repeats while held or misses rising edge' }
[void]$chord.Invoke($null, @($false))
if (!$chord.Invoke($null, @($true))) { throw 'Army shortcut does not rearm after release' }
[void]$chord.Invoke($null, @($false))
$menuPolicy = $armyMenu.GetMethod('AllowedMenu', $armyFlags)
if (!$menuPolicy.Invoke($null, [object[]]@($null)) -or !$menuPolicy.Invoke($null, @('army_wait')) -or $menuPolicy.Invoke($null, @('encounter')) -or $menuPolicy.Invoke($null, @('town'))) { throw 'Army wait menu is blocked or unsafe encounter/town menu allowed' }
$interface = $fix.GetType('BannerlordTogetherCrashGuard.ArmyInterfaceCompatibility', $true)
$selection = $interface.GetMethod('ValidSelection', $armyFlags)
foreach ($count in 11,20,64,512) {
    $ids = [string[]]@(1..$count | ForEach-Object { 'party-' + $_ })
    if (!$selection.Invoke($null, [object[]]@(,$ids))) { throw "Valid army selection of $count parties rejected" }
}
if ($selection.Invoke($null, [object[]]@(,[string[]]@('same','same'))) -or $selection.Invoke($null, [object[]]@(,[string[]]@('')))) { throw 'Invalid/duplicate party IDs accepted' }
$oldActiveHost = [BannerlordTogether.CoopSession]::IsHost
$oldActiveClient = [BannerlordTogether.CoopSession]::IsClient
try {
    [BannerlordTogether.CoopSession]::IsHost = $false
    [BannerlordTogether.CoopSession]::IsClient = $false
    if (!$interface.GetMethod('Done', $armyFlags).Invoke($null, [object[]]@($null))) { throw 'Single-player native army management was replaced' }
} finally {
    [BannerlordTogether.CoopSession]::IsHost = $oldActiveHost
    [BannerlordTogether.CoopSession]::IsClient = $oldActiveClient
}
$bridge = $fix.GetType('BannerlordTogetherCrashGuard.ArmyBattleCompatibility', $true)
$rows = [BannerlordTogether.BattlePartyRef[]]::new(2)
$leaderRow = [BannerlordTogether.BattlePartyRef]::new()
$leaderRow.PartyId = 'player_party'; $leaderRow.WorldPartySyncId = 44; $leaderRow.BattleSide = 0
$leaderRow.MemberCount = 100
$otherRow = [BannerlordTogether.BattlePartyRef]::new(); $otherRow.PartyId = 'npc_party'
$rows[0] = $leaderRow; $rows[1] = $otherRow
$mapped = $bridge.GetMethod('Remap', $armyFlags).Invoke($null, [object[]]@($rows, 'player_party', 'bt_ghost_player_party'))
if ($mapped[0].PartyId -ne 'bt_ghost_player_party' -or $rows[0].PartyId -ne 'player_party' -or
    $mapped[1].PartyId -ne 'npc_party' -or $mapped[0].WorldPartySyncId -ne 44 -or $mapped[0].MemberCount -ne 100 -or
    [object]::ReferenceEquals($rows, $mapped)) { throw 'Army leader mapping damaged the original manifest or other party data' }
$deploy = $fix.GetType('BannerlordTogetherCrashGuard.ClientDeploymentCompatibility', $true)
$setup = $deploy.GetNestedType('Setup', [Reflection.BindingFlags]::NonPublic)
$nativeSetup = [HarmonyLib.AccessTools]::Method([TaleWorlds.MountAndBlade.DeploymentMissionController], 'SetupTeams')
$originalSetup = [HarmonyLib.PatchProcessor]::GetOriginalInstructions($nativeSetup)
$rewriteSetup = @($setup.GetMethod('Transpiler', $armyFlags).Invoke($null, [object[]]@(,$originalSetup)))
if (@($rewriteSetup | Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand.Name -eq 'RequiredPlayer' }).Count -ne 1 -or
    @($rewriteSetup | Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand.Name -eq 'get_InitialPlayerAgent' }).Count -ne 0) { throw 'Deployment unsafe player dereference was not rewritten exactly once' }
if ($deploy.GetMethod('Expired', $armyFlags).Invoke($null, @([double]29.9)) -or !$deploy.GetMethod('Expired', $armyFlags).Invoke($null, @([double]30))) { throw 'Client deployment wait is unbounded or expires prematurely' }
$tick = $deploy.GetNestedType('Tick', [Reflection.BindingFlags]::NonPublic)
$unrelated = [InvalidOperationException]::new('unrelated')
if (![object]::ReferenceEquals($unrelated, $tick.GetMethod('Finalizer', $armyFlags).Invoke($null, [object[]]@($null,$unrelated)))) { throw 'Client deployment guard swallowed unrelated failures' }
$sides = $deploy.GetNestedType('Side', [Reflection.BindingFlags]::NonPublic).GetMethod('TargetMethods', $armyFlags).Invoke($null, $null)
if (@($sides).Count -ne 2) { throw 'Both field and siege deployment side guards must be targeted' }
Add-Type -ReferencedAssemblies @('netstandard', (Join-Path $gameBin 'TaleWorlds.MountAndBlade.dll'),
    (Join-Path $gameBin 'TaleWorlds.DotNet.dll'), (Join-Path $gameBin 'TaleWorlds.Engine.dll'),
    (Join-Path $gameBin 'TaleWorlds.CampaignSystem.dll'), (Join-Path $gameBin 'TaleWorlds.Core.dll'),
    (Join-Path $gameBin 'TaleWorlds.Library.dll'), (Join-Path $gameBin 'TaleWorlds.ObjectSystem.dll')) -TypeDefinition @'
public static class ArmyDeploymentFixture {
    public static TaleWorlds.MountAndBlade.Mission Current;
    public static bool MissionGetter(ref TaleWorlds.MountAndBlade.Mission __result) { __result = Current; return false; }
    public static bool HeroGetter(ref TaleWorlds.CampaignSystem.Hero __result) { __result = null; return false; }
    public static bool SpectatorGetter(ref bool __result) { __result = false; return false; }
    public static bool ScopeGetter(ref bool __result) { __result = true; return false; }
}
'@
$deploymentHarness = [HarmonyLib.Harmony]::new('btcg.tests.army-deployment')
$deploymentHarness.Patch([HarmonyLib.AccessTools]::PropertyGetter([TaleWorlds.MountAndBlade.Mission], 'Current'), [HarmonyLib.HarmonyMethod]::new([ArmyDeploymentFixture].GetMethod('MissionGetter')), $null, $null, $null) | Out-Null
$deploymentHarness.Patch([HarmonyLib.AccessTools]::PropertyGetter([TaleWorlds.CampaignSystem.Hero], 'MainHero'), [HarmonyLib.HarmonyMethod]::new([ArmyDeploymentFixture].GetMethod('HeroGetter')), $null, $null, $null) | Out-Null
$deploymentHarness.Patch([HarmonyLib.AccessTools]::PropertyGetter([BannerlordTogether.CoopSession], 'IsSpectator'), [HarmonyLib.HarmonyMethod]::new([ArmyDeploymentFixture].GetMethod('SpectatorGetter')), $null, $null, $null) | Out-Null
# Formatter-created Mission has no native handle and compares equal to null via
# DotNetObject's operators. Override only the scope gate for this managed state test.
# Real native-handle validity remains an in-game validation requirement.
$deploymentHarness.Patch($deploy.GetMethod('Scoped', $armyFlags), [HarmonyLib.HarmonyMethod]::new([ArmyDeploymentFixture].GetMethod('ScopeGetter')), $null, $null, $null) | Out-Null
$oldDeploymentClient = [BannerlordTogether.CoopSession]::IsClient
$oldDeploymentBattle = [BannerlordTogether.CoopSession]::InSpNativeBattle
try {
    [BannerlordTogether.CoopSession]::IsClient = $true
    [BannerlordTogether.CoopSession]::InSpNativeBattle = $true
    [ArmyDeploymentFixture]::Current = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.MountAndBlade.Mission])
    $controller = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.MountAndBlade.BattleDeploymentMissionController])
    [HarmonyLib.AccessTools]::Property([TaleWorlds.MountAndBlade.MissionBehavior], 'Mission').SetValue($controller, [ArmyDeploymentFixture]::Current)
    $pending = $null
    try { $deploy.GetMethod('RequiredPlayer', $armyFlags).Invoke($null, @([ArmyDeploymentFixture]::Current)) }
    catch { $pending = $_.Exception; while ($pending.InnerException) { $pending = $pending.InnerException } }
    if ($null -eq $pending -or $pending.GetType().Name -ne 'PendingPlayer') { throw 'Missing client body did not enter controlled wait path' }
    $waitResult = $tick.GetMethod('Finalizer', $armyFlags).Invoke($null, [object[]]@($controller,$pending))
    if ($null -ne $waitResult) { throw ('Controlled wait escaped deployment tick; scoped=' + $deploy.GetMethod('Scoped', $armyFlags).Invoke($null, @($controller)) + '; missionMatches=' + [object]::ReferenceEquals($controller.Mission, [TaleWorlds.MountAndBlade.Mission]::Current) + '; result=' + $waitResult.GetType().FullName) }
    if ($tick.GetMethod('Prefix', $armyFlags).Invoke($null, @($controller)) -or $controller.TeamSetupOver) { throw 'Waiting deployment was falsely marked complete or immediately rerun' }
    $sideGuard = $deploy.GetNestedType('Side', [Reflection.BindingFlags]::NonPublic)
    $sideGuard.GetMethod('Postfix', $armyFlags).Invoke($null, [object[]]@($controller,[TaleWorlds.Core.BattleSideEnum]::Defender,$true))
    if ($sideGuard.GetMethod('Prefix', $armyFlags).Invoke($null, [object[]]@($controller,[TaleWorlds.Core.BattleSideEnum]::Defender))) { throw 'Completed native side would be spawned again on resume' }
    if (!$sideGuard.GetMethod('Prefix', $armyFlags).Invoke($null, [object[]]@($controller,[TaleWorlds.Core.BattleSideEnum]::Attacker))) { throw 'Uninitialized native side was skipped' }
    'Managed client-controller regression passed: missing body yields a controlled wait, TeamSetupOver remains false, and completed sides are not rerun. No native agent was spawned.'
} finally {
    $deploymentHarness.UnpatchAll('btcg.tests.army-deployment')
    [BannerlordTogether.CoopSession]::IsClient = $oldDeploymentClient
    [BannerlordTogether.CoopSession]::InSpNativeBattle = $oldDeploymentBattle
}
'Army regressions passed: shortcut rearming, army_wait access, >10-party selection policy, native single-player preservation, local-only leader ID mapping, exact deployment player IL rewrite, bounded wait and unrelated-exception preservation. Live army/battle mutation remains unverified.'
