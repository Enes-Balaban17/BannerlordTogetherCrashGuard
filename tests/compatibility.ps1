param(
    [string]$GameDir,
    [string]$FixDll
)
$ErrorActionPreference = 'Stop'
$gameBin = Join-Path $GameDir 'bin\Win64_Shipping_Client'
$steamApps = Split-Path -Parent (Split-Path -Parent $GameDir)
$workshop = Join-Path $steamApps 'workshop\content\261550'
$script:dependencyDirs = @($gameBin)
foreach ($root in @((Join-Path $GameDir 'Modules'), $workshop)) {
    foreach ($module in Get-ChildItem -LiteralPath $root -Directory) {
        $bin = Join-Path $module.FullName 'bin\Win64_Shipping_Client'
        if (Test-Path -LiteralPath $bin) { $script:dependencyDirs += $bin }
    }
}
$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $name = [Reflection.AssemblyName]::new($eventArgs.Name).Name + '.dll'
    foreach ($directory in $script:dependencyDirs) {
        $candidate = Join-Path $directory $name
        if (Test-Path -LiteralPath $candidate) { return [Reflection.Assembly]::LoadFrom($candidate) }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
foreach ($name in 'Library','ObjectSystem','DotNet','Engine','Core','Localization','CampaignSystem','MountAndBlade','ScreenSystem','InputSystem','MountAndBlade.ViewModelCollection') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $gameBin "TaleWorlds.$name.dll"))
}
$nativeBin = Join-Path $GameDir 'Modules\Native\bin\Win64_Shipping_Client'
[void][Reflection.Assembly]::LoadFrom((Join-Path $nativeBin 'TaleWorlds.MountAndBlade.View.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $nativeBin 'TaleWorlds.MountAndBlade.GauntletUI.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $workshop '2859188632\bin\Win64_Shipping_Client\0Harmony.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $workshop '3761555398\bin\Win64_Shipping_Client\BannerlordTogether.dll'))
$optionalPaths = @(
    (Join-Path $workshop '2875138158\bin\Win64_Shipping_Client\XorberaxLegacy.dll'),
    (Join-Path $workshop '2875960358\bin\Win64_Shipping_Client\BetterTime.dll'),
    (Join-Path $workshop '3790263286\bin\Win64_Shipping_Client\ModularSmithing.Core.dll'),
    (Join-Path $workshop '3790263286\bin\Win64_Shipping_Client\ModularSmithing.Crafting.dll'),
    (Join-Path $workshop '3797156835\bin\Win64_Shipping_Client\BannerlordCustomCultures.dll'),
    (Join-Path $workshop '3798316554\bin\Win64_Shipping_Client\CustomStartingLoadout.dll'),
    (Join-Path $workshop '3796500121\bin\Win64_Shipping_Client\TogetherLordsCore.dll'),
    (Join-Path $GameDir 'Modules\ImprovedGarrisons\bin\Win64_Shipping_Client\ImprovedGarrisons.dll'),
    (Join-Path $GameDir 'Modules\CharacterReload\bin\Win64_Shipping_Client\CharacterReload.dll'),
    (Join-Path $GameDir 'Modules\DismembermentPlus\bin\Win64_Shipping_Client\DismembermentPlus.dll'),
    (Join-Path $GameDir 'Modules\OffTheClock.CampaignTweaks\bin\Win64_Shipping_Client\OffTheClock.CampaignTweaks.dll'),
    (Join-Path $GameDir 'Modules\Bannerlord.Diplomacy\bin\Win64_Shipping_Client\Bannerlord.Diplomacy.1.4.8.dll')
)
foreach ($path in $optionalPaths) {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing optional dependency for integration check $path" }
    [void][Reflection.Assembly]::LoadFrom($path)
}
$fix = [Reflection.Assembly]::LoadFrom($FixDll)
$flags = [Reflection.BindingFlags]'Static, NonPublic'
$owner = 'bannerlord.together.crashguard'
$harmony = [HarmonyLib.Harmony]::new($owner)
try {
    # Replay the missing-geography dereference against the original native method first.
    # This is a managed boundary regression, not a replay of the full campaign save.
    $nativeGeography = [HarmonyLib.AccessTools]::Method([TaleWorlds.CampaignSystem.GameComponents.DefaultSettlementValueModel], 'GeographicalAdvantageForFaction')
    $observedNativeNull = $false
    try { [void]$nativeGeography.Invoke($null, [object[]]@($null, $null)) }
    catch {
        $cause = $_.Exception
        while ($cause.InnerException) { $cause = $cause.InnerException }
        $observedNativeNull = $cause -is [NullReferenceException]
    }
    if (!$observedNativeNull) { throw 'Native missing-geography regression no longer reproduces; review target method' }
    'Reproduced native NullReferenceException at the missing-geography boundary before installing the guard.'
    $optional = $fix.GetType('BannerlordTogetherCrashGuard.OptionalCoopCompatibility', $true)
    $optional.GetMethod('Apply', $flags).Invoke($null, @($harmony))
    $required = @(
        'XorberaxLegacy.Logic.BanksofCalradiaCampaignBehavior.Withdraw',
        'XorberaxLegacy.Logic.BanksofCalradiaCampaignBehavior.UpdateBankData',
        'OffTheClock.CampaignTweaks.TweakWorkshopModel.get_MaximumWorkshopsPlayerCanHave',
        'OffTheClock.CampaignTweaks.TweakClanTierModel.GetCompanionLimit',
        'CharacterReload.Utils.HeroUtils.UpdateHeroCharacterBodyProperties',
        'CharacterReload.VM.HeroAdmin.HereAdminDashBoardVM.ExecuteDone',
        'ModularSmithing.Crafting.SmithingPersistenceBehavior.Register',
        'BannerlordTogether.GhostPartyController.ApplyHeroData',
        'BannerlordTogether.PlayerTradeBehavior.BeginSaveResync',
        'BannerlordTogether.CoopCampaignBehavior.PrepareGameplayPeerForIsolatedResync',
        'TaleWorlds.CampaignSystem.GameComponents.DefaultSettlementValueModel.GeographicalAdvantageForFaction',
        'Diplomacy.CampaignBehaviors.MaintainInfluenceBehavior.GetGoldValueForFief',
        'Diplomacy.CampaignBehaviors.MaintainInfluenceBehavior.GetBestFiefBarter',
        'Diplomacy.CampaignBehaviors.MaintainInfluenceBehavior.ReduceCorruption',
        'TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel.CalculateClanIncomeInternal',
        'TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel.AddPartyExpense',
        'TaleWorlds.CampaignSystem.CampaignBehaviors.ClanVariablesCampaignBehavior.DailyTickClan',
        'TaleWorlds.CampaignSystem.Actions.DeclareWarAction.ApplyInternal',
        'TaleWorlds.CampaignSystem.Actions.MakePeaceAction.Apply',
        'Diplomacy.Messengers.MessengerManager.StartDialogue',
        'Diplomacy.CampaignBehaviors.CivilWarBehavior.DailyTick',
        'ImprovedGarrisons.SaveSystem.GarrisonDailyBehavior.DailyBehavior',
        'ImprovedGarrisons.SaveSystem.GarrisonBehavior.HourlyEvent',
        'DismembermentPlus.Logic.DismembermentPlusMissionLogic.Dismember',
        'DismembermentPlus.Logic.DismembermentPlusMissionLogic.CheckSetSlowMotion',
        'BannerlordTogether.GhostPartyController.Despawn',
        'BannerlordTogether.GhostPartyController.Spawn',
        'BannerlordCustomCultures.CustomCultureBehavior.TryGetFeatOverrideResult',
        'CustomStartingLoadout.StartingLoadoutBehavior.OnCharacterCreationIsOver',
        'TogetherLordsCore.PeerSync.PacketDispatchPostfix.Postfix'
        'TaleWorlds.CampaignSystem.MapEvents.MapEvent.CalculateAndCommitMapEventResults'
        'BannerlordTogether.Network.CoopServer.BroadcastRawReliable'
        'BannerlordTogether.BattleSyncBehavior.SanitizeGhostAfterMergedBattle'
        'G.GM.C'
    )
    $actual = @($harmony.GetPatchedMethods() | ForEach-Object { "$($_.DeclaringType.FullName).$($_.Name)" })
    foreach ($target in $required) {
        if ($target -notin $actual) { throw "Optional integration target was not installed $target" }
    }
    "Optional integration bindings passed on $($actual.Count) actual game and mod methods."

    $cacheType = $fix.GetType('BannerlordTogetherCrashGuard.CompatibilityStateCache', $true)
    $cacheFlags = [Reflection.BindingFlags]'Instance, NonPublic'
    $cache = [Activator]::CreateInstance($cacheType, $true)
    $changed = $cacheType.GetMethod('Changed', $cacheFlags)
    $commit = $cacheType.GetMethod('Commit', $cacheFlags)
    $settingsState = [Collections.Generic.Dictionary[string,string]]::new()
    $settingsState.Add('setting/CampaignTweaks/CompanionBonus', '25')
    $settingsState.Add('setting/ModularSmithing2/Enabled', 'True')
    $initial = $changed.Invoke($cache, @($settingsState))
    if ($initial.Count -ne 2) { throw 'Joining peer must receive full settings' }
    if ($changed.Invoke($cache, @($settingsState)).Count -ne 2) { throw 'Unsent settings must remain eligible for retry' }
    $commit.Invoke($cache, @($initial))
    if ($changed.Invoke($cache, @($settingsState)).Count -ne 0) { throw 'Unchanged settings were retransmitted' }
    $settingsState['setting/CampaignTweaks/CompanionBonus'] = '30'
    $delta = $changed.Invoke($cache, @($settingsState))
    if ($delta.Count -ne 1 -or !$delta.ContainsKey('setting/CampaignTweaks/CompanionBonus')) { throw 'Host edit did not produce the exact settings delta' }
    $commit.Invoke($cache, @($delta))
    if ($changed.Invoke($cache, @($settingsState)).Count -ne 0) { throw 'Acknowledged edit was retransmitted' }
    $fresh = [Activator]::CreateInstance($cacheType, $true)
    if ($changed.Invoke($fresh, @($settingsState)).Count -ne 2) { throw 'Resync or reconnect must restore full state' }
    'Settings dispatch passed: join, unchanged silence, one changed value, failed-send retry and fresh resync.'

    $geography = [HarmonyLib.AccessTools]::Method([TaleWorlds.CampaignSystem.GameComponents.DefaultSettlementValueModel], 'GeographicalAdvantageForFaction')
    $oldHost = [BannerlordTogether.CoopSession]::IsHost
    $oldClient = [BannerlordTogether.CoopSession]::IsClient
    try {
        [BannerlordTogether.CoopSession]::IsHost = $true
        [BannerlordTogether.CoopSession]::IsClient = $false
        if ($geography.Invoke($null, [object[]]@($null, $null)) -ne 1.0) { throw 'Missing faction geography must use neutral multiplier' }
        $diplomacyType = [HarmonyLib.AccessTools]::TypeByName('Diplomacy.CampaignBehaviors.MaintainInfluenceBehavior')
        $valuation = [HarmonyLib.AccessTools]::Method($diplomacyType, 'GetGoldValueForFief')
        if ($valuation.Invoke($null, [object[]]@($null, $null)) -ne -1) { throw 'Missing buyer must not become a free fief valuation' }
        $barterGuard = $fix.GetType('BannerlordTogetherCrashGuard.DiplomacySettlementCompatibility').GetMethod('BarterPostfix', $flags)
        $invalidBarter = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.CampaignSystem.BarterSystem.Barterables.FiefBarterable])
        $barterArgs = [object[]]@($null, $invalidBarter)
        $barterGuard.Invoke($null, $barterArgs)
        if ($null -ne $barterArgs[1]) { throw 'Invalid buyer still produced a fief transfer' }
    } finally {
        [BannerlordTogether.CoopSession]::IsHost = $oldHost
        [BannerlordTogether.CoopSession]::IsClient = $oldClient
    }
    'Missing-faction geography and invalid fief buyer guards passed without creating or transferring a settlement.'

    $wire = $fix.GetType('BannerlordTogetherCrashGuard.CompatibilityWire', $true)
    $messageType = $fix.GetType('BannerlordTogetherCrashGuard.CompatibilityMessage', $true)
    $message = [Activator]::CreateInstance($messageType, $true)
    $messageType.GetField('Kind', [Reflection.BindingFlags]'Instance, NonPublic').SetValue($message, [byte]2)
    $messageType.GetField('Session', [Reflection.BindingFlags]'Instance, NonPublic').SetValue($message, 'session-test')
    $values = $messageType.GetField('Values', [Reflection.BindingFlags]'Instance, NonPublic').GetValue($message)
    $values.Add('setting/CampaignTweaks/CompanionBonus', '25')
    $values.Add('name', 'Çağrı ile co-op')
    $encode = $wire.GetMethod('Encode', $flags)
    $decode = $wire.GetMethod('Decode', $flags)
    $bytes = $encode.Invoke($null, @($message))
    $copy = $decode.Invoke($null, [object[]]@(,$bytes))
    $copiedValues = $messageType.GetField('Values', [Reflection.BindingFlags]'Instance, NonPublic').GetValue($copy)
    if ($copiedValues['name'] -cne 'Çağrı ile co-op') { throw 'UTF-8 protocol round trip failed' }
    foreach ($bad in @([byte[]]@(0,1,2), [byte[]]($bytes + 9))) {
        $rejected = $false
        try { [void]$decode.Invoke($null, [object[]]@(,$bad)) } catch { $rejected = $true }
        if (!$rejected) { throw 'Malformed or foreign protocol envelope was accepted' }
    }
    'Protocol round trip, foreign packet and trailing-data rejection passed.'

    Add-Type -TypeDefinition @'
public sealed class BankFixture
{
    public int Balance { get; set; }
    public float InterestRate { get; set; }
    public int RemainingUnpaidLoan { get; set; }
}
public enum SettingsMode { Normal, Shared }
public sealed class SettingsFixture
{
    public int CompanionBonus;
    public bool Enabled;
    public float Factor;
    public SettingsMode Mode;
    public static object Current;
    public static object Get() { return Current; }
    public static object Missing() { return null; }
}
public static class ConservationFixture
{
    public static void CheckIncome(System.Reflection.MethodInfo split)
    {
        for (int players = 2; players <= 4; players++)
        for (int total = -1024; total <= 1024; total++)
        {
            int sum = 0, minimum = int.MaxValue, maximum = int.MinValue;
            for (int index = 0; index < players; index++)
            {
                int share = (int)split.Invoke(null, new object[] { (float)total, players, index });
                sum += share;
                minimum = System.Math.Min(minimum, share);
                maximum = System.Math.Max(maximum, share);
            }
            if (sum != total || maximum - minimum > 1)
                throw new System.Exception("Income allocation lost or duplicated money");
        }
    }
    public static void Check(System.Reflection.MethodInfo split)
    {
        for (int players = 2; players <= 4; players++)
        for (int total = 0; total <= 1024; total++)
        for (int start = 0; start < players; start++)
        {
            int[] shares = (int[])split.Invoke(null, new object[] { total, players, start });
            int sum = 0, maximum = 0, minimum = int.MaxValue, owners = 0;
            foreach (int amount in shares)
            {
                if (amount < 0) throw new System.Exception("Negative share");
                sum += amount;
                maximum = System.Math.Max(maximum, amount);
                minimum = System.Math.Min(minimum, amount);
                if (amount > 0) owners++;
            }
            if (sum != total || maximum - minimum > 1)
                throw new System.Exception("Allocation lost or duplicated rewards");
            if (total == 1 && (owners != 1 || maximum != 1))
                throw new System.Exception("A unique lord was allocated to multiple owners");
        }
    }
}
'@
    $normalize = $optional.GetMethod('NormalizeEmptyBank', $flags)
    $bank = [BankFixture]::new()
    $bank.Balance = 0
    $bank.InterestRate = 875
    $bank.RemainingUnpaidLoan = 900
    $normalize.Invoke($null, @($bank))
    if ($bank.InterestRate -ne 0 -or $bank.RemainingUnpaidLoan -ne 900) { throw 'Empty-bank interest repair failed' }
    $bank.Balance = 1500
    $bank.InterestRate = 80
    $normalize.Invoke($null, @($bank))
    if ($bank.InterestRate -ne 80 -or $bank.Balance -ne 1500) { throw 'Funded bank account changed' }
    'Empty-bank regression passed and positive balance and loan data were preserved.'

    $reward = $fix.GetType('BannerlordTogetherCrashGuard.RewardShareCompatibility', $true)
    [ConservationFixture]::Check($reward.GetMethod('SplitCount', $flags))
    'Reward conservation and single-owner allocation passed for 2-4 players, every remainder, and 0-1024 units.'
    $sanitize = [HarmonyLib.AccessTools]::Method([BannerlordTogether.BattleSyncBehavior], 'SanitizeGhostAfterMergedBattle')
    $instructions = [HarmonyLib.PatchProcessor]::GetOriginalInstructions($sanitize)
    $rewritten = $reward.GetMethod('PreserveAssignedLords', $flags).Invoke($null, [object[]]@(,$instructions))
    $guarded = @($rewritten | Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand.Name -eq 'ReleaseUnassignedPrisoner' })
    if ($guarded.Count -ne 2) { throw 'Both ghost cleanup release paths must preserve assigned lords' }
    'Lord custody cleanup rewrite passed on the installed Bannerlord Together binary.'

    $finance = $fix.GetType('BannerlordTogetherCrashGuard.ClanIncomeCompatibility', $true)
    [ConservationFixture]::CheckIncome($finance.GetMethod('GrossShare', $flags))
    $finance.GetField('PlayerCount', $flags).SetValue($null, 2)
    $finance.GetField('HostIncome', $flags).SetValue($null, [single]0)
    $incomeCall = $finance.GetNestedType('IncomeCall', [Reflection.BindingFlags]'NonPublic')
    $call = [Activator]::CreateInstance($incomeCall, $true)
    $incomeCall.GetField('Before', [Reflection.BindingFlags]'Instance, NonPublic').SetValue($call, [single]100)
    $collected = [TaleWorlds.CampaignSystem.ExplainedNumber]::new([single]1100, $false, $null)
    # The asset wallet may already be empty here. Split the captured 1000 income,
    # preserving the incoming 100 balance; do not query a second, now-empty wallet.
    $incomeArgs = [object[]]@($null, $collected, $false, $call)
    $finance.GetMethod('IncomePostfix', $flags).Invoke($null, $incomeArgs)
    if ($incomeArgs[1].ResultNumber -ne 600) { throw 'Collected income was recomputed after withdrawal or shared twice' }
    if ($finance.GetField('HostIncome', $flags).GetValue($null) -ne 1000) { throw 'Host did not retain the actual gross collection' }
    $incomeCall.GetField('ClientHandled', [Reflection.BindingFlags]'Instance, NonPublic').SetValue($call, $true)
    $incomeArgs = [object[]]@($null, [TaleWorlds.CampaignSystem.ExplainedNumber]::new([single]600, $false, $null), $false, $call)
    $finance.GetMethod('IncomePostfix', $flags).Invoke($null, $incomeArgs)
    if ($incomeArgs[1].ResultNumber -ne 600) { throw 'Client income was shared twice' }
    $incomeArgs = [object[]]@($null, [TaleWorlds.CampaignSystem.ExplainedNumber]::new([single]1100, $false, $null), $false, $null)
    $finance.GetMethod('IncomePostfix', $flags).Invoke($null, $incomeArgs)
    if ($incomeArgs[1].ResultNumber -ne 1100) { throw 'Unrelated clan finance was modified' }
    $finance.GetField('PlayerCount', $flags).SetValue($null, 1)
    'Income regression passed: post-withdrawal collection, no double share, unrelated clan isolation and conserved signed amounts for 2-4 players.'
    $dailyMethod = [HarmonyLib.AccessTools]::Method([TaleWorlds.CampaignSystem.CampaignBehaviors.ClanVariablesCampaignBehavior], 'DailyTickClan')
    $dailyInstructions = [HarmonyLib.PatchProcessor]::GetOriginalInstructions($dailyMethod)
    $dailyRewrite = $finance.GetMethod('RouteDailyPayment', $flags).Invoke($null, [object[]]@(,$dailyInstructions))
    $dailyCalls = @($dailyRewrite | Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand.Name -eq 'PayDailyGold' })
    if ($dailyCalls.Count -ne 1) { throw 'Daily gold must be routed once without duplicate payment' }
    'Native daily finance routing passed: exactly one existing gold transfer is replaced.'

    $settings = $fix.GetType('BannerlordTogetherCrashGuard.HostSettingsCompatibility', $true)
    $bindings = $settings.GetField('Bindings', $flags).GetValue($null)
    $instanceFlags = [Reflection.BindingFlags]'Instance, NonPublic'
    $missing = [Delegate]::CreateDelegate([Func[object]], [SettingsFixture].GetMethod('Missing'))
    foreach ($binding in $bindings) { $binding.GetType().GetField('GetInstance', $instanceFlags).SetValue($binding, $missing) }
    $getter = [Delegate]::CreateDelegate([Func[object]], [SettingsFixture].GetMethod('Get'))
    $bindings[0].GetType().GetField('GetInstance', $instanceFlags).SetValue($bindings[0], $getter)
    $hostSettings = [SettingsFixture]::new()
    $hostSettings.CompanionBonus = 25
    $hostSettings.Enabled = $true
    $hostSettings.Factor = 2.5
    $hostSettings.Mode = [SettingsMode]::Shared
    [SettingsFixture]::Current = $hostSettings
    $snapshot = [Collections.Generic.Dictionary[string,string]]::new()
    $settings.GetMethod('Capture', $flags).Invoke($null, @($snapshot))
    $clientSettings = [SettingsFixture]::new()
    $clientSettings.CompanionBonus = 3
    $clientSettings.Factor = 1.0
    [SettingsFixture]::Current = $clientSettings
    $settings.GetMethod('Apply', $flags).Invoke($null, @($snapshot))
    if ($clientSettings.CompanionBonus -ne 25 -or !$clientSettings.Enabled -or
        $clientSettings.Factor -ne 2.5 -or $clientSettings.Mode -ne [SettingsMode]::Shared) { throw 'Host settings were not applied' }
    $settings.GetMethod('Restore', $flags).Invoke($null, $null)
    if ($clientSettings.CompanionBonus -ne 3 -or $clientSettings.Enabled -or
        $clientSettings.Factor -ne 1.0 -or $clientSettings.Mode -ne [SettingsMode]::Normal) { throw 'Local settings were not restored' }
    'Host configuration transfer and disconnect restoration passed for integer, Boolean, float and enum settings.'
} finally {
    $harmony.UnpatchAll($owner)
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
