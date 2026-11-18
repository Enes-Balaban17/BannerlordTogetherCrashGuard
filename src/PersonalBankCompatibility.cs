using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    // The native save has one bank dictionary for MainHero. Remote humans must never
    // inherit that dictionary as their own balance. Only the host changes this ledger.
    internal sealed class PersonalBankCompatibility : CampaignBehaviorBase
    {
        public sealed class Account
        {
            public bool Open { get; set; }
            public int Balance { get; set; }
            public float Interest { get; set; }
            public double LastDay { get; set; }
        }
        public sealed class Request
        {
            public string Id { get; set; }
            public string Action { get; set; }
            public string Town { get; set; }
            public int Amount { get; set; }
        }
        public sealed class Result
        {
            public string Id { get; set; }
            public int GoldDelta { get; set; }
            public string Message { get; set; }
        }
        internal static PersonalBankCompatibility Instance;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2000000 };
        private Dictionary<string, Dictionary<string, Account>> _accounts = new Dictionary<string, Dictionary<string, Account>>();
        private readonly Dictionary<string, Result> _results = new Dictionary<string, Result>();
        private readonly HashSet<string> _applied = new HashSet<string>();
        private readonly HashSet<string> _acked = new HashSet<string>();
        private string _pending;
        private CampaignTimeControlMode _clientBeforePause;
        private bool _resync;
        private float _resyncDelay;
        private int _resyncAttempts;
        private CampaignTimeControlMode _beforePause;
        private bool _pauseOwned;
        private DateTime _ackDeadline;
        private static Type BankType => AccessTools.TypeByName("XorberaxLegacy.Logic.BanksofCalradiaCampaignBehavior");
        private static object Native => BankType == null ? null : AccessTools.Property(BankType, "Current")?.GetValue(null);
        private static object Settings => BankType == null ? null : AccessTools.Field(BankType, "_settings")?.GetValue(null);
        private static IDictionary Banks => Native == null ? null : AccessTools.Field(BankType, "_settlementBankDataBySettlementId").GetValue(Native) as IDictionary;
        private static double Setting(string name) => Convert.ToDouble(AccessTools.Property(Settings.GetType(), name).GetValue(Settings), CultureInfo.InvariantCulture);
        private static void Set(object target, string key, object value) => AccessTools.Property(target.GetType(), key).SetValue(target, value);

        public PersonalBankCompatibility() { Instance = this; }
        public override void RegisterEvents() => CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Daily);
        public override void SyncData(IDataStore store)
        {
            string data = store.IsSaving ? Json.Serialize(_accounts) : "";
            store.SyncData("btcg_personal_bank_accounts_v1", ref data);
            string receipts = store.IsSaving ? Json.Serialize(_results) : "";
            store.SyncData("btcg_personal_bank_receipts_v1", ref receipts);
            if (store.IsLoading)
            {
                _accounts = string.IsNullOrEmpty(data) ? new Dictionary<string, Dictionary<string, Account>>() :
                    Json.Deserialize<Dictionary<string, Dictionary<string, Account>>>(data);
                _results.Clear(); _acked.Clear();
                if (!string.IsNullOrEmpty(receipts))
                    foreach (var pair in Json.Deserialize<Dictionary<string, Result>>(receipts)) _results.Add(pair.Key, pair.Value);
                _acked.UnionWith(_results.Keys);
            }
        }
        internal static void Install(Harmony harmony)
        {
            if (BankType == null) return;
            foreach (string action in new[] { "Deposit", "Withdraw", "CloseBankAccountAtSettlement", "TryOpenBankAccountAtSettlement" })
                harmony.Patch(AccessTools.Method(BankType, action), new HarmonyMethod(typeof(PersonalBankCompatibility), nameof(BeforeTransaction)),
                    new HarmonyMethod(typeof(PersonalBankCompatibility), nameof(AfterTransaction)));
            harmony.Patch(AccessTools.Method(BankType, "UpdateBankData"), new HarmonyMethod(typeof(PersonalBankCompatibility), nameof(BeforeInterest)));
            harmony.Patch(AccessTools.Method(BankType, "GetBankDataAtSettlement"), postfix: new HarmonyMethod(typeof(PersonalBankCompatibility), nameof(BankRead)));
            // Native loan quests target MainHero and cannot safely be assigned to a ghost.
            harmony.Patch(AccessTools.Method(BankType, "TakeOutLoan"), new HarmonyMethod(typeof(PersonalBankCompatibility), nameof(BeforeLoan)));
            harmony.Patch(AccessTools.Method(BankType, "RepayLoanAtSettlement"), new HarmonyMethod(typeof(PersonalBankCompatibility), nameof(BeforeLoan)));
        }
        private static bool BeforeLoan()
        {
            if (!CoopSession.IsClient || !CoopSession.IsActive) return true;
            InformationManager.DisplayMessage(new InformationMessage("[Crash Guard] Personal co-op banking supports deposits and withdrawals. Native loan quests are host-only."));
            return false;
        }
        private static bool BeforeInterest() => !CoopSession.IsClient || !CoopSession.IsActive;
        private static void BankRead(object __result)
        {
            if (CoopSession.IsActive) OptionalCoopCompatibility.NormalizeEmptyBank(__result);
        }
        private static bool BeforeTransaction(MethodBase __originalMethod, object[] __args, out int[] __state)
        {
            __state = null;
            if (!CoopSession.IsActive || Instance == null) return true;
            if (CoopSession.IsHost)
            {
                __state = new[] { Hero.MainHero.Gold };
                return true;
            }
            if (!CoopCompatibilityBehavior.HasHostState || Instance._pending != null)
            {
                InformationManager.DisplayMessage(new InformationMessage("[Crash Guard] Wait for the bank transaction / host synchronization."));
                return false;
            }
            Settlement town = __args.OfType<Settlement>().FirstOrDefault();
            if (town == null) return false;
            string id = Guid.NewGuid().ToString("N");
            Instance._pending = id;
            Instance._clientBeforePause = Campaign.Current.TimeControlMode;
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
            var request = new Request { Id = id, Action = __originalMethod.Name, Town = town.StringId,
                Amount = __args.OfType<int>().FirstOrDefault() };
            CoopCompatibilityBehavior.Instance.PublishChanges(new Dictionary<string, string> { ["bank/request"] = Json.Serialize(request) });
            InformationManager.DisplayMessage(new InformationMessage("[Crash Guard] Bank transaction sent to host; awaiting resync."));
            return false;
        }
        private static void AfterTransaction(int[] __state)
        {
            if (__state != null && Hero.MainHero.Gold != __state[0]) Instance?.ScheduleResync();
        }
        private Dictionary<string, Account> Accounts(string identity)
        {
            if (!_accounts.TryGetValue(identity, out var accounts))
                _accounts.Add(identity, accounts = new Dictionary<string, Account>());
            return accounts;
        }
        internal void Capture(int peer, Dictionary<string, string> values)
        {
            if (Native == null) return;
            string identity = CoopPeerIdentity.CharacterId(peer);
            if (!string.IsNullOrEmpty(identity)) values["bank/accounts"] = Json.Serialize(Accounts(identity));
        }
        internal void Receive(int peer, Dictionary<string, string> values)
        {
            if (Native == null) return;
            string identity = CoopPeerIdentity.CharacterId(peer);
            if (string.IsNullOrEmpty(identity)) return;
            if (values.TryGetValue("bank/ack", out string ack))
            {
                string ackKey = identity + "/" + ack;
                if (_results.TryGetValue(ackKey, out var result) && result.Message == "accepted" && _acked.Add(ackKey)) ScheduleResync();
                return;
            }
            if (!values.TryGetValue("bank/request", out string json)) return;
            Request request = Json.Deserialize<Request>(json);
            if (request == null || request.Id == null || request.Id.Length != 32 || !Guid.TryParseExact(request.Id, "N", out _) ||
                string.IsNullOrEmpty(request.Town) || request.Town.Length > 128) return;
            string key = identity + "/" + request.Id;
            if (!_results.TryGetValue(key, out Result response))
            {
                if (_resync || _results.Count >= 4096)
                {
                    CoopCompatibilityBehavior.Instance.SendState(peer, new Dictionary<string, string> {
                        ["bank/result"] = Json.Serialize(new Result { Id = request.Id, Message = "rejected: resync in progress; retry after it finishes" }) });
                    return;
                }
                Hero human = CoopPeerIdentity.HeroForPeer(peer);
                Settlement town = Settlement.Find(request.Town);
                response = new Result { Id = request.Id, Message = "rejected: player must be at an accessible town bank" };
                var party = human?.PartyBelongedTo;
                bool near = town?.Town != null && party != null &&
                    (party.CurrentSettlement == town || (party.Position - town.Position).LengthSquared <= 4f);
                if (near && !human.IsPrisoner && !human.IsDead && party.MapEvent == null && town.SiegeEvent == null &&
                    !FactionManager.IsAtWarAgainstFaction(human.MapFaction, town.MapFaction))
                {
                    var accounts = Accounts(identity);
                    if (!accounts.TryGetValue(town.StringId, out Account account))
                        accounts.Add(town.StringId, account = new Account { LastDay = CampaignTime.Now.ToDays });
                    var previous = new Account { Open = account.Open, Balance = account.Balance, Interest = account.Interest, LastDay = account.LastDay };
                    int previousGold = human.Gold;
                    response = Transact(account, request, human.Gold, (int)Setting("MaxBankBalance"),
                        (int)((double)Setting("BankAccountOpeningCostBase") + Math.Round(town.Town.Prosperity * Setting("BankAccountOpeningCostSettlementProsperityFactor"))),
                        CampaignTime.Now.ToDays);
                    if (response.Message == "accepted")
                    {
                        try
                        {
                            float interest = account.Balance > 0 ? Interest(town, account.Balance) : 0;
                            Pause();
                            human.ChangeHeroGold(response.GoldDelta);
                            account.Interest = interest;
                            _ackDeadline = DateTime.UtcNow.AddSeconds(10);
                            BattleLog.Write("[BANK] host committed personal transaction player=" + identity + " town=" + town.StringId +
                                " action=" + request.Action + " delta=" + response.GoldDelta + " balance=" + account.Balance);
                        }
                        catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error))
                        {
                            account.Open = previous.Open; account.Balance = previous.Balance;
                            account.Interest = previous.Interest; account.LastDay = previous.LastDay;
                            if (human.Gold != previousGold) human.ChangeHeroGold(previousGold - human.Gold);
                            if (_pauseOwned) Campaign.Current.TimeControlMode = _beforePause;
                            _pauseOwned = false;
                            response = new Result { Id = request.Id, Message = "rejected: transaction rolled back; see battle.log" };
                            BattleLog.Write("[BANK] transaction rolled back " + error);
                        }
                    }
                }
                _results.Add(key, response);
            }
            var reply = new Dictionary<string, string> { ["bank/result"] = Json.Serialize(response) };
            Capture(peer, reply);
            CoopCompatibilityBehavior.Instance.SendState(peer, reply);
        }
        internal static Result Transact(Account account, Request request, int gold, int maximum, int openingCost, double day)
        {
            var result = new Result { Id = request.Id, Message = "rejected: insufficient balance, capacity, or account state" };
            int amount = request.Amount;
            if (request.Action == "TryOpenBankAccountAtSettlement" && !account.Open && openingCost >= 0 && gold >= openingCost)
            { account.Open = true; account.LastDay = day; result.GoldDelta = -openingCost; }
            else if (request.Action == "Deposit" && account.Open && amount > 0 && amount <= gold && (long)account.Balance + amount <= maximum)
            { account.Balance += amount; result.GoldDelta = -amount; }
            else if (request.Action == "Withdraw" && account.Open && amount > 0 && amount <= account.Balance && (long)gold + amount <= int.MaxValue)
            { account.Balance -= amount; result.GoldDelta = amount; }
            else if (request.Action == "CloseBankAccountAtSettlement" && account.Open && (long)gold + account.Balance <= int.MaxValue)
            { result.GoldDelta = account.Balance; account.Balance = 0; account.Open = false; }
            else return result;
            if (account.Balance == 0) account.Interest = 0;
            result.Message = "accepted";
            return result;
        }
        internal void Apply(Dictionary<string, string> values)
        {
            if (values.TryGetValue("bank/accounts", out string json) && Banks is IDictionary banks)
            {
                // Never import the host's native account or a previous peer's account.
                var accounts = Json.Deserialize<Dictionary<string, Account>>(json);
                Type valueType = banks.GetType().GetGenericArguments()[1];
                foreach (string key in banks.Keys.Cast<string>().Where(key => !accounts.ContainsKey(key)).ToArray()) banks.Remove(key);
                foreach (var pair in accounts)
                {
                    object bank = banks.Contains(pair.Key) ? banks[pair.Key] : Activator.CreateInstance(valueType, true);
                    Set(bank, "SettlementId", pair.Key); Set(bank, "HasAccount", pair.Value.Open);
                    Set(bank, "Balance", pair.Value.Balance); Set(bank, "InterestRate", pair.Value.Interest);
                    Set(bank, "RemainingUnpaidLoan", 0); Set(bank, "OriginalLoanAmount", 0); Set(bank, "LoanQuest", null);
                    Set(bank, "LastInterestAccrualDate", CampaignTime.Days((float)pair.Value.LastDay));
                    banks[pair.Key] = bank;
                }
            }
            if (!values.TryGetValue("bank/result", out string responseJson)) return;
            Result response = Json.Deserialize<Result>(responseJson);
            if (response.Id != _pending) return;
            if (_applied.Add(response.Id) && response.Message == "accepted") Hero.MainHero.ChangeHeroGold(response.GoldDelta);
            _pending = null;
            InformationManager.DisplayMessage(new InformationMessage("[Crash Guard] Bank: " + response.Message));
            if (response.Message == "accepted")
                CoopCompatibilityBehavior.Instance.PublishChanges(new Dictionary<string, string> { ["bank/ack"] = response.Id });
            else Campaign.Current.TimeControlMode = _clientBeforePause;
        }
        private static float Interest(Settlement town, int balance) => (float)Math.Max(0,
            Math.Round((balance + town.Town.Prosperity) * Setting("InterestRatePerSettlementProsperityFactor")));
        private void Daily()
        {
            try { AccrueDaily(); }
            catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error))
            { BattleLog.Write("[BANK] daily personal accrual failed; balances retained: " + error); }
        }
        private void AccrueDaily()
        {
            if (!CoopSession.IsHost || Native == null) return;
            double day = CampaignTime.Now.ToDays;
            int maximum = (int)Setting("MaxBankBalance");
            foreach (var accounts in _accounts.Values)
                foreach (var pair in accounts)
                {
                    Account account = pair.Value;
                    if (!account.Open || account.Balance <= 0 || day - account.LastDay < Setting("InterestAccrualRateInDays")) continue;
                    Settlement town = Settlement.Find(pair.Key);
                    if (town?.Town == null) continue;
                    account.Balance = (int)Math.Min(maximum, (long)account.Balance + (long)Math.Max(0, account.Interest));
                    account.Interest = Interest(town, account.Balance);
                    account.LastDay = day;
                }
            foreach (int peer in CoopCompatibilityBehavior.Instance.CompatiblePeers())
            {
                var values = new Dictionary<string, string>(); Capture(peer, values);
                CoopCompatibilityBehavior.Instance.SendState(peer, values);
            }
        }
        private void ScheduleResync()
        {
            if (_resync) return;
            Pause();
            _resync = true; _resyncDelay = 0.2f; _resyncAttempts = 0;
        }
        private void Pause()
        {
            if (!_pauseOwned) _beforePause = Campaign.Current.TimeControlMode;
            _pauseOwned = true;
            Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        }
        internal void Tick(float dt)
        {
            if (_pauseOwned && !_resync && CoopSession.IsHost && DateTime.UtcNow >= _ackDeadline)
            {
                BattleLog.Write("[BANK] client acknowledgement timed out; requesting authoritative save-resync");
                ScheduleResync();
            }
            if (!_resync || !CoopSession.IsHost || Mission.Current != null) return;
            _resyncDelay -= dt;
            if (_resyncDelay > 0) return;
            object[] args = { null, null };
            bool started = false;
            try
            {
                started = PlayerTradeBehavior.Instance != null &&
                    (bool)AccessTools.Method(typeof(PlayerTradeBehavior), "BeginSaveResync").Invoke(PlayerTradeBehavior.Instance, args);
            }
            catch (Exception error) when (DeploymentCrashGuard.CanSuppress(error))
            { args[1] = error.GetBaseException().Message; }
            if (started) { _resync = false; _pauseOwned = false; return; }
            if (++_resyncAttempts < 5) { _resyncDelay = 1f; return; }
            _resync = false;
            Campaign.Current.TimeControlMode = _beforePause;
            _pauseOwned = false;
            BattleLog.Write("[BANK] transaction committed but native resync refused: " + args[1]);
            InformationManager.DisplayMessage(new InformationMessage("[Crash Guard] Bank saved; automatic resync unavailable: " + args[1] + ". Use BT Sync."));
        }
    }
}
