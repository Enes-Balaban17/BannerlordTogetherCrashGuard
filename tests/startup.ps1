param([string]$GameDir, [string]$FixDll, [switch]$ExpectRegression)
$ErrorActionPreference = 'Stop'
$gameBin = Join-Path $GameDir 'bin\Win64_Shipping_Client'
$steamApps = Split-Path -Parent (Split-Path -Parent $GameDir)
$workshop = Join-Path $steamApps 'workshop\content\261550'
$script:startupDirs = @($gameBin, (Join-Path $GameDir 'Modules\SandBox\bin\Win64_Shipping_Client'),
    (Join-Path $workshop '2859188632\bin\Win64_Shipping_Client'),
    (Join-Path $workshop '3761555398\bin\Win64_Shipping_Client'))
$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $name = [Reflection.AssemblyName]::new($eventArgs.Name).Name + '.dll'
    foreach ($directory in $script:startupDirs) {
        $path = Join-Path $directory $name
        if (Test-Path -LiteralPath $path) { return [Reflection.Assembly]::LoadFrom($path) }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
foreach ($name in 'Library','ObjectSystem','DotNet','Engine','Localization','Core','CampaignSystem','MountAndBlade') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $gameBin "TaleWorlds.$name.dll"))
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $script:startupDirs[2] '0Harmony.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $script:startupDirs[3] 'BannerlordTogether.dll'))
$fix = [Reflection.Assembly]::LoadFrom($FixDll)
$flags = [Reflection.BindingFlags]'Static, NonPublic'
$owner = 'bannerlord.together.crashguard'
$harmony = [HarmonyLib.Harmony]::new($owner)
$gameField = [HarmonyLib.AccessTools]::Field([TaleWorlds.Core.Game], '_current')
$oldGame = $gameField.GetValue($null)
try {
    if ($null -ne [TaleWorlds.Core.Game]::Current) { throw 'Startup regression must begin before Game.Current exists.' }
    $optional = $fix.GetType('BannerlordTogetherCrashGuard.OptionalCoopCompatibility', $true)
    $optional.GetMethod('Apply', $flags).Invoke($null, @($harmony))
    if (!$ExpectRegression) {
        $fix.GetType('BannerlordTogetherCrashGuard.ClanIncomeCompatibility').GetMethod('Install', $flags).Invoke($null, @($harmony))
    }
    $early = @($harmony.GetPatchedMethods() | Where-Object { $_.DeclaringType -eq [TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel] })
    if (!$ExpectRegression -and $early.Count -ne 0) { throw 'Finance was patched before Game.Current.' }
    if ($ExpectRegression) {
        # Harmony/runtime versions do not all force .cctor at the same point.
        # Explicitly replay the native initialization boundary recorded by CrashDoctor.
        try { [Runtime.CompilerServices.RuntimeHelpers]::RunClassConstructor([TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel].TypeHandle) }
        catch { }
    }
    # Do not launch a game: supply only the managed state the real finance .cctor reads.
    $game = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.Core.Game])
    $campaign = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.CampaignSystem.Campaign])
    [HarmonyLib.AccessTools]::Property([TaleWorlds.Core.Game], 'GameType').SetValue($game, $campaign)
    $gameField.SetValue($null, $game)
    if (!$ExpectRegression) {
        $optional.GetMethod('ApplyCampaign', $flags).Invoke($null, @($harmony))
        $notReady = @($harmony.GetPatchedMethods() | Where-Object { $_.DeclaringType -eq [TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel] })
        if ($notReady.Count -ne 0) { throw 'Finance was patched before GameTextManager.' }
        $fix.GetType('BannerlordTogetherCrashGuard.ClanIncomeCompatibility').GetMethod('Install', $flags).Invoke($null, @($harmony))
        $directEarly = @($harmony.GetPatchedMethods() | Where-Object { $_.DeclaringType -eq [TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel] })
        if ($directEarly.Count -ne 0) { throw 'Direct finance installation bypassed the readiness guard.' }
    }
    $texts = [TaleWorlds.Core.GameTextManager]::new()
    $texts.AddGameText('str_finance_projects_income').AddVariation('startup-finance-fixture')
    [HarmonyLib.AccessTools]::Property([TaleWorlds.Core.Game], 'GameTextManager').SetValue($game, $texts)
    if (!$ExpectRegression) { $optional.GetMethod('ApplyCampaign', $flags).Invoke($null, @($harmony)) }
    $poisoned = $false
    try { [Runtime.CompilerServices.RuntimeHelpers]::RunClassConstructor([TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel].TypeHandle) }
    catch {
        $errorCause = $_.Exception
        while ($errorCause.InnerException) { $errorCause = $errorCause.InnerException }
        if ($errorCause -isnot [NullReferenceException]) { throw }
        $poisoned = $true
    }
    if ($ExpectRegression) {
        if (!$poisoned) { throw 'Old startup failure did not reproduce.' }
        'Replayed the reported native failure: initializing DefaultClanFinanceModel without Game.Current permanently poisons it; adding Game.Current later does not recover it. The initialization boundary was invoked explicitly in this headless regression.'
    } else {
        if ($poisoned) { throw 'Finance initializer is still poisoned.' }
        $optional.GetMethod('ApplyCampaign', $flags).Invoke($null, @($harmony))
        $method = [HarmonyLib.AccessTools]::Method([TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel], 'CalculateClanIncomeInternal')
        if ([HarmonyLib.Harmony]::GetPatchInfo($method).Owners -notcontains $owner) { throw 'Deferred finance adapter not installed.' }
        $before = [HarmonyLib.Harmony]::GetPatchInfo($method).Prefixes.Count
        $optional.GetMethod('ApplyCampaign', $flags).Invoke($null, @($harmony))
        if ([HarmonyLib.Harmony]::GetPatchInfo($method).Prefixes.Count -ne $before) { throw 'Repeated campaign event duplicated finance hooks.' }
        $text = [HarmonyLib.AccessTools]::Field([TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel], '_projectsIncomeText').GetValue($null)
        if ($text.ToString() -ne 'startup-finance-fixture') { throw 'Native finance labels were initialized before game texts.' }
        'Startup lifecycle passed: no finance patches without Game.Current/text manager; actual native initializer succeeds with game texts; campaign hooks install once. No game launched.'
    }
} finally {
    $harmony.UnpatchAll($owner)
    $gameField.SetValue($null, $oldGame)
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
