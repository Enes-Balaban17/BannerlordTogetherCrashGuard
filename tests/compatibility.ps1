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
    $earlyFinance = @($harmony.GetPatchedMethods() | Where-Object { $_.DeclaringType -eq [TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel] })
    if ($earlyFinance.Count -ne 0) { throw 'Finance hooks were installed before the campaign game context' }
    $currentGameField = [HarmonyLib.AccessTools]::Field([TaleWorlds.Core.Game], '_current')
    $previousGame = $currentGameField.GetValue($null)
    try {
        $campaignGame = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.Core.Game])
        $campaignType = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.CampaignSystem.Campaign])
        [HarmonyLib.AccessTools]::Property([TaleWorlds.Core.Game], 'GameType').SetValue($campaignGame, $campaignType)
        [HarmonyLib.AccessTools]::Property([TaleWorlds.Core.Game], 'GameTextManager').SetValue($campaignGame, [TaleWorlds.Core.GameTextManager]::new())
        $currentGameField.SetValue($null, $campaignGame)
        $optional.GetMethod('ApplyCampaign', $flags).Invoke($null, @($harmony))
        [Runtime.CompilerServices.RuntimeHelpers]::RunClassConstructor([TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel].TypeHandle)
    } finally { $currentGameField.SetValue($null, $previousGame) }
    $required = @(
        'XorberaxLegacy.Logic.BanksofCalradiaCampaignBehavior.Withdraw',
        'XorberaxLegacy.Logic.BanksofCalradiaCampaignBehavior.UpdateBankData',
        'XorberaxLegacy.Logic.BanksofCalradiaCampaignBehavior.Deposit',
        'XorberaxLegacy.Logic.BanksofCalradiaCampaignBehavior.TryOpenBankAccountAtSettlement',
        'BannerlordTogether.SpNativeBattle.SpNativeBattleClientMissionBehavior.ApplySyntheticFatalBlow',
        'BannerlordTogether.ArmySyncBehavior.TryEnsurePlayerArmyForLeader',
        'BannerlordTogether.ArmySyncBehavior.CreateClientArmy',
        'BannerlordTogether.ArmySyncBehavior.ApplyBatch',
        'BannerlordTogether.BattleSyncBehavior.TryTriggerArmyBattle',
        'BannerlordTogether.BattleSyncBehavior.SendLocalBattleStart',
        'TaleWorlds.CampaignSystem.ViewModelCollection.ArmyManagement.ArmyManagementVM.ExecuteDone',
        'TaleWorlds.CampaignSystem.ViewModelCollection.ArmyManagement.ArmyManagementVM.ExecuteDisbandArmy',
        'TaleWorlds.CampaignSystem.GameComponents.DefaultArmyManagementCalculationModel.CanPlayerCreateArmy',
        'TaleWorlds.CampaignSystem.EncounterManager.StartPartyEncounter',
        'TaleWorlds.CampaignSystem.Army.HourlyTick',
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
        'TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel.CalculateClanExpensesInternal',
        'TaleWorlds.CampaignSystem.GameComponents.DefaultClanFinanceModel.AddIncomeFromParty',
        'BannerlordTogether.ClientCaravanCreationBehavior.HasDailyFinanceDeltaSent',
        'TaleWorlds.CampaignSystem.Settlements.Workshops.Workshop.ChangeOwnerOfWorkshop',
        'OffTheClock.CampaignTweaks.SubModule.LoadSettings',
        'ModularSmithing.Crafting.MSmithConfig.Save',
        'ModularSmithing.Crafting.MSmithConfig.Reload',
        'TaleWorlds.CampaignSystem.Actions.DestroyClanAction.ApplyInternal',
        'TaleWorlds.CampaignSystem.LogEntries.ClanDestroyedLogEntry.get_IsVisibleNotification',
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
public sealed class AssetWalletFixture
{
    public int Balance;
    public int Withdrawals;
    public int Collect(bool withdraw)
    {
        int amount = Balance;
        if (withdraw) { Balance = 0; Withdrawals++; }
        return amount;
    }
    public System.Func<bool, int> Callback() { return Collect; }
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

    $ledgerType = $fix.GetType('BannerlordTogetherCrashGuard.IncomeAssetLedger', $true)
    $ledger = [Activator]::CreateInstance($ledgerType, $true)
    $ledgerFlags = [Reflection.BindingFlags]'Instance, NonPublic'
    $addAsset = $ledgerType.GetMethod('Add', $ledgerFlags)
    $collectAssets = $ledgerType.GetMethod('Collect', $ledgerFlags)
    $hostShop = [AssetWalletFixture]::new(); $hostShop.Balance = 300
    $clientShop = [AssetWalletFixture]::new(); $clientShop.Balance = 500
    $clientCaravan = [AssetWalletFixture]::new(); $clientCaravan.Balance = 700
    $duplicateProjection = [AssetWalletFixture]::new(); $duplicateProjection.Balance = 500
    $addAsset.Invoke($ledger, [object[]]@('workshop/town-1/0', $hostShop.Callback()))
    $addAsset.Invoke($ledger, [object[]]@('workshop/town-2/1', $clientShop.Callback()))
    $addAsset.Invoke($ledger, [object[]]@('caravan/peer-caravan-1', $clientCaravan.Callback()))
    $addAsset.Invoke($ledger, [object[]]@('workshop/town-2/1', $duplicateProjection.Callback()))
    if ($collectAssets.Invoke($ledger, [object[]]@($false)) -ne 1500) { throw 'Combined human assets or canonical deduplication failed' }
    if ($hostShop.Withdrawals + $clientShop.Withdrawals + $clientCaravan.Withdrawals -ne 0) { throw 'Preview withdrew asset funds' }
    if ($collectAssets.Invoke($ledger, [object[]]@($true)) -ne 1500) { throw 'Actual pooled asset collection differs from preview' }
    if ($hostShop.Withdrawals -ne 1 -or $clientShop.Withdrawals -ne 1 -or $clientCaravan.Withdrawals -ne 1 -or $duplicateProjection.Withdrawals -ne 0) { throw 'An asset was collected twice or an owner was missed' }
    if ($hostShop.Balance + $clientShop.Balance + $clientCaravan.Balance -ne 0) { throw 'Wallet withdrawals were not conserved' }

    $expenseCall = $finance.GetNestedType('ExpenseCall', [Reflection.BindingFlags]'NonPublic')
    $expenseState = [Activator]::CreateInstance($expenseCall, $true)
    $expenseCall.GetField('Garrison', [Reflection.BindingFlags]'Instance, NonPublic').SetValue($expenseState, [single]2000)
    $finance.GetField('PlayerCount', $flags).SetValue($null, 2)
    $expensePostfix = $finance.GetMethod('ExpensesPostfix', $flags)
    $previousHost = [BannerlordTogether.CoopSession]::IsHost
    try {
        [BannerlordTogether.CoopSession]::IsHost = $true
        $expenseArgs = [object[]]@([TaleWorlds.CampaignSystem.ExplainedNumber]::new([single]1800, $false, $null), $false, $expenseState)
        $expensePostfix.Invoke($null, $expenseArgs)
        if ($expenseArgs[0].ResultNumber -ne 800) { throw 'Host shared garrison expense or personal expense accounting is wrong' }
        [BannerlordTogether.CoopSession]::IsHost = $false
        $finance.GetField('ShareIndex', $flags).SetValue($null, 1)
        $clientExpenseState = [Activator]::CreateInstance($expenseCall, $true)
        $expenseArgs = [object[]]@([TaleWorlds.CampaignSystem.ExplainedNumber]::new([single]1600, $false, $null), $false, $clientExpenseState)
        $expensePostfix.Invoke($null, $expenseArgs)
        if ($expenseArgs[0].ResultNumber -ne 600) { throw 'Client did not use the same host garrison total' }
    } finally {
        [BannerlordTogether.CoopSession]::IsHost = $previousHost
        $finance.GetField('PlayerCount', $flags).SetValue($null, 1)
        $finance.GetField('ShareIndex', $flags).SetValue($null, 0)
    }
    'Shared assets passed: host/client workshops and caravans, canonical deduplication, preview without withdrawal, one collection per asset, shared garrison totals and separate personal expenses.'

    Add-Type -ReferencedAssemblies @('netstandard', 'System.Core', (Join-Path $gameBin 'TaleWorlds.CampaignSystem.dll'), (Join-Path $gameBin 'TaleWorlds.Core.dll'), (Join-Path $gameBin 'TaleWorlds.Library.dll'), (Join-Path $gameBin 'TaleWorlds.ObjectSystem.dll')) -TypeDefinition @'
public static class IdleSyncFixture
{
    public static TaleWorlds.CampaignSystem.Campaign Campaign;
    public static int Publishes;
    public static int Resyncs;
    public static bool ResyncPrefix(ref bool __result, ref string reason) { Resyncs++; reason = "fixture"; __result = false; return false; }
    public static bool CampaignGetter(ref TaleWorlds.CampaignSystem.Campaign __result) { __result = Campaign; return false; }
    public static bool PeerGetter(ref int[] __result) { __result = new int[] { 1 }; return false; }
    public static bool PublishPrefix(object __instance)
    {
        Publishes++;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        __instance.GetType().GetField("_settingsDirty", flags).SetValue(__instance, false);
        __instance.GetType().GetField("_runtimeDirty", flags).SetValue(__instance, false);
        ((System.Collections.Generic.HashSet<int>)__instance.GetType().GetField("_fullSync", flags).GetValue(__instance)).Clear();
        return false;
    }
}
public sealed class ScalarSettingFixture { public int Value { get; set; } }
'@
    $behaviorType = $fix.GetType('BannerlordTogetherCrashGuard.CoopCompatibilityBehavior', $true)
    $behavior = [Activator]::CreateInstance($behaviorType, $true)
    [void][Activator]::CreateInstance($fix.GetType('BannerlordTogetherCrashGuard.PersonalBankCompatibility'), $true)
    $instanceFlags = [Reflection.BindingFlags]'Instance, NonPublic'
    $server = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([BannerlordTogether.Network.CoopServer])
    $behaviorType.GetField('_network', $instanceFlags).SetValue($behavior, $server)
    $behaviorType.GetField('_peers', $instanceFlags).GetValue($behavior).Add(1, [uint64]1)
    $behaviorType.GetField('_settingsDirty', $instanceFlags).SetValue($behavior, $false)
    $behaviorType.GetField('_runtimeDirty', $instanceFlags).SetValue($behavior, $false)
    [IdleSyncFixture]::Campaign = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.CampaignSystem.Campaign])
    $idleOwner = [HarmonyLib.Harmony]::new('btcg.tests.idle')
    $idleOwner.Patch([HarmonyLib.AccessTools]::PropertyGetter([TaleWorlds.CampaignSystem.Campaign], 'Current'), [HarmonyLib.HarmonyMethod]::new([IdleSyncFixture].GetMethod('CampaignGetter')), $null, $null, $null) | Out-Null
    $idleOwner.Patch([HarmonyLib.AccessTools]::PropertyGetter([BannerlordTogether.Network.CoopServer], 'GameplayPeerIds'), [HarmonyLib.HarmonyMethod]::new([IdleSyncFixture].GetMethod('PeerGetter')), $null, $null, $null) | Out-Null
    $idleOwner.Patch($behaviorType.GetMethod('Publish', $instanceFlags), [HarmonyLib.HarmonyMethod]::new([IdleSyncFixture].GetMethod('PublishPrefix')), $null, $null, $null) | Out-Null
    $idleOwner.Patch([HarmonyLib.AccessTools]::Method([BannerlordTogether.PlayerTradeBehavior], 'BeginSaveResync'), [HarmonyLib.HarmonyMethod]::new([IdleSyncFixture].GetMethod('ResyncPrefix')), $null, $null, $null) | Out-Null
    $previousHost = [BannerlordTogether.CoopSession]::IsHost
    $previousClient = [BannerlordTogether.CoopSession]::IsClient
    $previousServer = [BannerlordTogether.CoopSession]::Server
    $previousState = [BannerlordTogether.CoopSession]::State
    try {
        [BannerlordTogether.CoopSession]::IsHost = $true
        [BannerlordTogether.CoopSession]::IsClient = $false
        [BannerlordTogether.CoopSession]::Server = $server
        [BannerlordTogether.CoopSession]::State = [BannerlordTogether.CoopSessionState]::Ready
        $tick = $behaviorType.GetMethod('TickCore', $instanceFlags)
        for ($frame = 0; $frame -lt 400; $frame++) { $tick.Invoke($behavior, [object[]]@([single]0.1)) }
        if ([IdleSyncFixture]::Publishes -ne 0) { throw 'Idle gameplay still triggers periodic full/settings publishing' }
        if ([IdleSyncFixture]::Resyncs -ne 0) { throw 'Idle personal bank triggered a save-resync' }
        $behaviorType.GetMethod('MarkSettingsChanged', $instanceFlags).Invoke($behavior, $null)
        for ($frame = 0; $frame -lt 10; $frame++) { $tick.Invoke($behavior, [object[]]@([single]0.1)) }
        if ([IdleSyncFixture]::Publishes -ne 1) { throw 'A host setting edit did not cause exactly one publish' }
        $behaviorType.GetMethod('RequestFullSync', $instanceFlags).Invoke($behavior, [object[]]@([int]1))
        for ($frame = 0; $frame -lt 10; $frame++) { $tick.Invoke($behavior, [object[]]@([single]0.1)) }
        if ([IdleSyncFixture]::Publishes -ne 2) { throw 'Explicit resync did not cause exactly one publish' }
        $watcher = $fix.GetType('BannerlordTogetherCrashGuard.HostSettingsCompatibility')
        $idleOwner.Patch([ScalarSettingFixture].GetProperty('Value').GetSetMethod(),
            [HarmonyLib.HarmonyMethod]::new($watcher.GetMethod('SettingBefore', $flags)),
            [HarmonyLib.HarmonyMethod]::new($watcher.GetMethod('SettingAfter', $flags)), $null, $null) | Out-Null
        $scalar = [ScalarSettingFixture]::new()
        $scalar.Value = 9
        for ($frame = 0; $frame -lt 10; $frame++) { $tick.Invoke($behavior, [object[]]@([single]0.1)) }
        if ([IdleSyncFixture]::Publishes -ne 3) { throw 'A real scalar setter change was not dispatched' }
        for ($frame = 0; $frame -lt 400; $frame++) {
            $scalar.Value = 9
            $tick.Invoke($behavior, [object[]]@([single]0.1))
        }
        if ([IdleSyncFixture]::Publishes -ne 3) { throw 'Unchanged scalar setter writes still cause repeated publishing' }
    } finally {
        [BannerlordTogether.CoopSession]::IsHost = $previousHost
        [BannerlordTogether.CoopSession]::IsClient = $previousClient
        [BannerlordTogether.CoopSession]::Server = $previousServer
        [BannerlordTogether.CoopSession]::State = $previousState
        $idleOwner.UnpatchAll('btcg.tests.idle')
        $behaviorType.GetField('Instance', $flags).SetValue($null, $null)
    }
    'Actual compatibility tick passed: 40 seconds idle produced zero publishes; one setting edit and one explicit resync produced exactly one publish each.'

    $equipmentBehavior = $fix.GetType('BannerlordTogetherCrashGuard.CompanionEquipmentBehavior', $true)
    $budgetMethod = $equipmentBehavior.GetMethod('PurchaseBudget', $flags)
    foreach ($case in @(@(500,0), @(1000,0), @(1500,150), @(10000,1000), @(500000,20000))) {
        if ($budgetMethod.Invoke($null, [object[]]@([int]$case[0])) -ne $case[1]) { throw 'Companion reserve or purchase budget was violated' }
    }
    $itemView = $fix.GetType('BannerlordTogetherCrashGuard.CompanionEquipment.ItemView')
    $category = [Enum]::ToObject($fix.GetType('BannerlordTogetherCrashGuard.CompanionEquipment.ItemCategory'), 0)
    $rank = $fix.GetType('BannerlordTogetherCrashGuard.CompanionEquipment.ItemRanking').GetMethod('IsBetter')
    $worn = [Activator]::CreateInstance($itemView, [object[]]@('worn', $category, [single]100, [int]3, 'one_handed', [int]0))
    $worse = [Activator]::CreateInstance($itemView, [object[]]@('worse', $category, [single]90, [int]5, 'one_handed', [int]0))
    $better = [Activator]::CreateInstance($itemView, [object[]]@('better', $category, [single]110, [int]3, 'one_handed', [int]0))
    if ($rank.Invoke($null, [object[]]@($worse,$worn)) -or !$rank.Invoke($null, [object[]]@($better,$worn))) { throw 'Companion ranking would replace stronger worn gear with a worse item' }
    $syntheticClan = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.CampaignSystem.Clan])
    [HarmonyLib.AccessTools]::Property([TaleWorlds.CampaignSystem.Clan], 'StringId').SetValue($syntheticClan, 'bt_remote_client_player_faction')
    $notification = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.CampaignSystem.LogEntries.ClanDestroyedLogEntry])
    [HarmonyLib.AccessTools]::Field([TaleWorlds.CampaignSystem.LogEntries.ClanDestroyedLogEntry], '_clan').SetValue($notification, $syntheticClan)
    $oldHost = [BannerlordTogether.CoopSession]::IsHost
    try {
        [BannerlordTogether.CoopSession]::IsHost = $true
        if ($notification.IsVisibleNotification) { throw 'Synthetic co-op clan still produces an extinction notification' }
        $destroy = [HarmonyLib.AccessTools]::Method([TaleWorlds.CampaignSystem.Actions.DestroyClanAction], 'ApplyInternal')
        $detail = [Enum]::ToObject($destroy.GetParameters()[1].ParameterType, 0)
        $destroy.Invoke($null, [object[]]@($syntheticClan,$detail))
        $realClan = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([TaleWorlds.CampaignSystem.Clan])
        [HarmonyLib.AccessTools]::Property([TaleWorlds.CampaignSystem.Clan], 'StringId').SetValue($realClan, 'ordinary_npc_clan')
        [HarmonyLib.AccessTools]::Field([TaleWorlds.CampaignSystem.LogEntries.ClanDestroyedLogEntry], '_clan').SetValue($notification, $realClan)
        if (!$notification.IsVisibleNotification) { throw 'Genuine NPC clan extinction notifications were hidden' }
    } finally { [BannerlordTogether.CoopSession]::IsHost = $oldHost }
    if (!$notification.IsVisibleNotification) { throw 'Single-player notifications were changed by a co-op-only guard' }
    $speedType = $fix.GetType('BannerlordTogetherCrashGuard.SpeedCompatibility', $true)
    $betterTimeType = [HarmonyLib.AccessTools]::TypeByName('BetterTime.Settings')
    $betterTimeSettings = $betterTimeType.GetProperty('Instance', [Reflection.BindingFlags]'Static, Public, FlattenHierarchy').GetValue($null)
    $extraSpeed = [single]8
    if ($null -ne $betterTimeSettings) { $extraSpeed = [single][HarmonyLib.AccessTools]::Property($betterTimeType, 'ExtraFastForwardMultiplier').GetValue($betterTimeSettings) }
    if (!$speedType.GetMethod('IsExtra', $flags).Invoke($null, [object[]]@($extraSpeed)) -or
        $speedType.GetMethod('IsExtra', $flags).Invoke($null, [object[]]@([single]($extraSpeed + 0.5)))) { throw 'Configured Extra Fast multiplier was not recognized' }
    'Companion budget/upgrade ranking and synthetic clan destruction/notification guards passed; single-player notifications remain active.'

    $bankBehavior = $fix.GetType('BannerlordTogetherCrashGuard.PersonalBankCompatibility', $true)
    $accountType = $bankBehavior.GetNestedType('Account')
    $requestType = $bankBehavior.GetNestedType('Request')
    $account = [Activator]::CreateInstance($accountType)
    $account.Open = $true; $account.Balance = 1000; $account.Interest = 50
    $request = [Activator]::CreateInstance($requestType)
    $request.Id = 'regression'; $request.Action = 'Withdraw'; $request.Amount = 400
    $transaction = $bankBehavior.GetMethod('Transact', $flags)
    $result = $transaction.Invoke($null, [object[]]@($account, $request, 100, 1000000, 1000, [double]10))
    if ($result.GoldDelta -ne 400 -or $account.Balance -ne 600) { throw 'Host personal withdrawal did not debit principal' }
    $request.Amount = 600
    $result = $transaction.Invoke($null, [object[]]@($account, $request, 500, 1000000, 1000, [double]10))
    if ($account.Balance -ne 0 -or $account.Interest -ne 0 -or $result.GoldDelta -ne 600) { throw 'Empty personal account retained interest' }
    $result = $transaction.Invoke($null, [object[]]@($account, $request, 1100, 1000000, 1000, [double]10))
    if ($result.GoldDelta -ne 0 -or $result.Message -eq 'accepted') { throw 'Stale withdrawal minted gold' }
    $request.Action = 'Deposit'; $request.Amount = -1
    $result = $transaction.Invoke($null, [object[]]@($account, $request, 1100, 1000000, 1000, [double]10))
    if ($result.Message -eq 'accepted') { throw 'Negative deposit accepted' }
    $request.Amount = 1200
    if ($transaction.Invoke($null, [object[]]@($account, $request, 1100, 1000000, 1000, [double]10)).Message -eq 'accepted') { throw 'Unfunded deposit accepted' }
    $request.Amount = 500
    $result = $transaction.Invoke($null, [object[]]@($account, $request, 1100, 1000000, 1000, [double]10))
    if ($account.Balance -ne 500 -or $result.GoldDelta -ne -500) { throw 'Personal deposit failed conservation' }
    Add-Type -ReferencedAssemblies @((Join-Path $gameBin 'TaleWorlds.CampaignSystem.dll')) -TypeDefinition @'
public sealed class BankSaveFixture : TaleWorlds.CampaignSystem.IDataStore {
    public System.Collections.Generic.Dictionary<string, string> Data = new System.Collections.Generic.Dictionary<string, string>();
    public bool Saving;
    public bool IsSaving { get { return Saving; } }
    public bool IsLoading { get { return !Saving; } }
    public bool SyncData<T>(string key, ref T data) {
        if (Saving) Data[key] = (string)(object)data;
        else if (Data.ContainsKey(key)) data = (T)(object)Data[key];
        return true;
    }
}
'@
    $personal = [Activator]::CreateInstance($bankBehavior, $true)
    $accountFlags = [Reflection.BindingFlags]'Instance, NonPublic'
    $accountsMethod = $bankBehavior.GetMethod('Accounts', $accountFlags)
    $first = $accountsMethod.Invoke($personal, @('character-A'))
    $second = $accountsMethod.Invoke($personal, @('character-B'))
    $first.Add('town-A', $account)
    $otherAccount = [Activator]::CreateInstance($accountType); $otherAccount.Open = $true; $otherAccount.Balance = 77
    $second.Add('town-A', $otherAccount)
    $journal = $bankBehavior.GetField('_results', $accountFlags).GetValue($personal)
    $journal.Add('character-A/regression', $result)
    $save = [BankSaveFixture]::new(); $save.Saving = $true
    $personal.SyncData($save)
    $reloaded = [Activator]::CreateInstance($bankBehavior, $true)
    $save.Saving = $false; $reloaded.SyncData($save)
    $a = $accountsMethod.Invoke($reloaded, @('character-A'))
    $b = $accountsMethod.Invoke($reloaded, @('character-B'))
    if ($a['town-A'].Balance -ne 500 -or $b['town-A'].Balance -ne 77 -or [object]::ReferenceEquals($a['town-A'], $b['town-A'])) { throw 'Personal bank accounts leaked across identities/save load' }
    if (!$bankBehavior.GetField('_results', $accountFlags).GetValue($reloaded).ContainsKey('character-A/regression')) { throw 'Transaction receipt was lost on save/resync' }
    $death = $fix.GetType('BannerlordTogetherCrashGuard.BattleDeathCompatibility')
    $unexpected = [InvalidOperationException]::new('must propagate')
    if (![object]::ReferenceEquals($unexpected, $death.GetMethod('FinalizeReplay', $flags).Invoke($null, @($unexpected)))) { throw 'Death guard swallowed an unrelated exception' }
    $oldClient = [BannerlordTogether.CoopSession]::IsClient
    $oldBattle = [BannerlordTogether.CoopSession]::InSpNativeBattle
    try {
        [BannerlordTogether.CoopSession]::IsClient = $true
        [BannerlordTogether.CoopSession]::InSpNativeBattle = $true
        if ($death.GetMethod('Before', $flags).Invoke($null, [object[]]@($null, $null))) { throw 'Missing death visuals still enter RegisterBlow' }
        if ($null -ne $death.GetMethod('FinalizeReplay', $flags).Invoke($null, @([NullReferenceException]::new('replay')))) { throw 'Synthetic death null reference escaped' }
        [BannerlordTogether.CoopSession]::InSpNativeBattle = $false
        $outside = [NullReferenceException]::new('outside battle')
        if (![object]::ReferenceEquals($outside, $death.GetMethod('FinalizeReplay', $flags).Invoke($null, @($outside)))) { throw 'Outside-battle null reference was swallowed' }
    } finally {
        [BannerlordTogether.CoopSession]::IsClient = $oldClient
        [BannerlordTogether.CoopSession]::InSpNativeBattle = $oldBattle
    }
    $army = $fix.GetType('BannerlordTogetherCrashGuard.ClanArmyCompatibility')
    if ($army.GetMethod('Available', $flags).Invoke($null, [object[]]@($null))) { throw 'Missing party can enter clan army' }
    'Personal banking passed: funded debit/credit conservation, stale/negative rejection, zero-balance interest and isolated per-character save/load. Army/death hook bindings and unrelated-exception preservation passed.'

    . (Join-Path $PSScriptRoot 'army.ps1')
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
