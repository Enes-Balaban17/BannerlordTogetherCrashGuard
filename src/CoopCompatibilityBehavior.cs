using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BannerlordTogether;
using BannerlordTogether.Network;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal sealed class CoopCompatibilityBehavior : CampaignBehaviorBase
    {
        internal static CoopCompatibilityBehavior Instance;
        internal static bool Applying;
        internal static bool HasHostState;
        internal static float HostSpeed = 4f;
        internal static string HostClan;
        internal static int HostCompanions;
        internal static int HostWorkshops;

        private readonly Dictionary<int, ulong> _peers = new Dictionary<int, ulong>();
        private readonly Dictionary<int, string> _peerCharacters = new Dictionary<int, string>();
        private readonly Dictionary<int, CompatibilityStateCache> _sentState = new Dictionary<int, CompatibilityStateCache>();
        private readonly HashSet<int> _fullSync = new HashSet<int>();
        private bool _requestFullSync;
        private readonly string _session = Guid.NewGuid().ToString("N");
        private CoopNetworkBase _network;
        private long _revision;
        private long _lastRevision;
        private string _hostSession;
        private float _timer;
        private bool _settingsDirty = true;
        private bool _runtimeDirty;
        private float _settingsDebounce;
        private bool _financeDirty = true;
        private DateTime _lastSlowPublish;
        private float _requestTimer;
        private readonly HashSet<string> _reportedErrors = new HashSet<string>();
        private string _retiredGhosts = "";
        private string _patrolProjections = "";

        public CoopCompatibilityBehavior()
        {
            Instance = this;
            GhostEncyclopediaCompatibility.Retired.Clear();
            GarrisonsCompatibility.Projections.Clear();
            CultureCompatibility.BeginCampaign();
            RewardShareCompatibility.BeginCampaign();
        }

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            GhostEncyclopediaCompatibility.ImportLegacyLogEvidence();
            GhostEncyclopediaCompatibility.RestoreHiddenEntries();
        }

        public override void SyncData(IDataStore dataStore)
        {
            CultureCompatibility.SyncData(dataStore);
            RewardShareCompatibility.SyncData(dataStore);
            if (dataStore.IsSaving)
            {
                _retiredGhosts = string.Join("\n", GhostEncyclopediaCompatibility.Retired);
                _patrolProjections = string.Join("\n", GarrisonsCompatibility.Projections);
            }
            dataStore.SyncData("btcg_retired_ghosts_v1", ref _retiredGhosts);
            dataStore.SyncData("btcg_patrol_projections_v1", ref _patrolProjections);
            if (dataStore.IsLoading)
            {
                GhostEncyclopediaCompatibility.Retired.UnionWith((_retiredGhosts ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
                GarrisonsCompatibility.Projections.UnionWith((_patrolProjections ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        internal void Tick(float dt)
        {
            try { TickCore(dt); }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            { Report("tick", exception); }
        }

        private void TickCore(float dt)
        {
            _requestTimer -= dt;
            CoopNetworkBase network = CoopSession.IsHost
                ? (CoopNetworkBase)CoopSession.Server : CoopSession.Client;
            if (!ReferenceEquals(_network, network))
            {
                Reset();
                _network = network;
            }
            if (!CoopSession.IsActive)
            {
                if (HasHostState || _peers.Count > 0)
                    Reset();
                return;
            }
            DismembermentCompatibility.Tick();
            if (Campaign.Current == null || Mission.Current != null || CoopSession.InSpNativeBattle || !CoopSession.IsSessionReady)
                return;
            var receiveBudget = System.Diagnostics.Stopwatch.StartNew();
            int received = 0;
            while (received++ < 4 && receiveBudget.ElapsedMilliseconds < 2 && CompatibilityWire.TryReceive(out var incoming, out var envelope))
            {
                if (!ReferenceEquals(incoming, network))
                    continue;
                try { Receive(envelope); }
                catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
                { Report("receive", exception); }
            }
            RewardShareCompatibility.Tick();
            AppearanceCompatibility.TickFullStates();
            if (_requestFullSync && CoopSession.IsClient && HasHostState)
            {
                CoopSession.Client?.SendRaw(CompatibilityWire.Encode(new CompatibilityMessage { Kind = 5 }));
                _requestFullSync = false;
            }
            _timer -= dt;
            _settingsDebounce -= dt;
            try
            {
                if (CoopSession.IsHost && CoopSession.Server != null)
                {
                    if (_timer <= 0f)
                    {
                        _timer = 2f;
                        foreach (int peer in _peers.Keys.Except(CoopSession.Server.GameplayPeerIds).ToArray())
                        {
                            _peers.Remove(peer);
                            _peerCharacters.Remove(peer);
                            _sentState.Remove(peer);
                            _fullSync.Remove(peer);
                            _runtimeDirty = _financeDirty = true;
                        }
                        ClanIncomeCompatibility.PlayerCount = 1 + _peers.Count;
                    }
                    if (_peers.Count > 0 && _settingsDebounce <= 0f &&
                        (_settingsDirty || _runtimeDirty || _fullSync.Count > 0))
                        Publish();
                }
                else if (CoopSession.IsClient && !CoopSession.IsSpectator)
                {
                    if (!HasHostState && _timer <= 0f)
                    {
                        _timer = 2f;
                        var hello = new CompatibilityMessage { Kind = 1 };
                        hello.Values["culture"] = CultureCompatibility.LocalProfile();
                        CoopSession.Client?.SendRaw(CompatibilityWire.Encode(hello));
                    }
                }
            }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            { Report("publish", exception); }
        }

        private void Reset()
        {
            HostSettingsCompatibility.Restore();
            HasHostState = false;
            _hostSession = null;
            _lastRevision = 0;
            _peers.Clear();
            _peerCharacters.Clear();
            _sentState.Clear();
            _fullSync.Clear();
            _requestFullSync = false;
            CultureCompatibility.LocalCharacterId = null;
            ClanIncomeCompatibility.PlayerCount = 1;
            ClanIncomeCompatibility.ShareIndex = 0;
            SharedAssetIncome.Reset();
            _timer = 0;
            _settingsDirty = true;
            _runtimeDirty = false;
            _settingsDebounce = 0;
            _financeDirty = true;
            _reportedErrors.Clear();
            AppearanceCompatibility.Reset();
            SmithingCompatibility.Reset();
        }

        private void Report(string phase, Exception exception)
        {
            string signature = phase + exception.GetType().FullName + exception.Message;
            if (_reportedErrors.Add(signature))
                BattleLog.Write("[COOP-COMPAT] " + phase + " failed: " + exception);
        }

        private void Receive(NetworkEnvelope envelope)
        {
            CompatibilityMessage message = CompatibilityWire.Decode(envelope.Data);
            if (CoopSession.IsHost)
            {
                if (CoopSession.Server == null || !CoopSession.Server.GameplayPeerIds.Contains(envelope.PeerId))
                    return;
                if (message.Kind == 1)
                {
                    string identity = CoopPeerIdentity.CharacterId(envelope.PeerId);
                    if (string.IsNullOrEmpty(identity))
                        return;
                    bool added = !_peers.TryGetValue(envelope.PeerId, out ulong oldEpoch) || oldEpoch != envelope.PeerConnectionEpoch ||
                        !_peerCharacters.TryGetValue(envelope.PeerId, out string oldCharacter) || oldCharacter != identity;
                    _peers[envelope.PeerId] = envelope.PeerConnectionEpoch;
                    _peerCharacters[envelope.PeerId] = identity;
                    // A native save-resync can reload the client campaign while retaining
                    // the same connection epoch. Its new behavior still needs a welcome.
                    _sentState.Remove(envelope.PeerId);
                    BattleLog.Write("[COOP-COMPAT] peer handshake accepted id=" + envelope.PeerId + " newIdentity=" + added);
                    if (added && message.Values.TryGetValue("culture", out string profile))
                        CultureCompatibility.Accept(envelope.PeerId, profile, false);
                    var welcome = Capture();
                    welcome["character"] = identity;
                    welcome["income-index"] = (Array.IndexOf(_peers.Keys.OrderBy(id => id).ToArray(), envelope.PeerId) + 1).ToString(CultureInfo.InvariantCulture);
                    CultureCompatibility.Capture(welcome, _peers.Keys);
                    SmithingCompatibility.Capture(welcome);
                    GarrisonsCompatibility.Capture(welcome);
                    RewardShareCompatibility.Capture(welcome, _peers.Keys);
                    SendState(envelope.PeerId, welcome);
                    AppearanceCompatibility.SendFullState(envelope.PeerId);
                    _runtimeDirty = true;
                }
                else if (_peers.TryGetValue(envelope.PeerId, out ulong epoch) && epoch == envelope.PeerConnectionEpoch)
                {
                    if (message.Kind == 5)
                        RequestFullSync(envelope.PeerId);
                    else if (message.Kind == 3 && CoopSession.AllowClientTimeControl &&
                        message.Values.TryGetValue("speed", out string text) &&
                        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float speed) &&
                        !float.IsNaN(speed) && speed >= 1f && speed <= 128f)
                        Campaign.Current.SpeedUpMultiplier = speed;
                    else if (message.Kind == 4)
                    {
                        AppearanceCompatibility.AcceptClientChanges(message.Values);
                        SmithingCompatibility.AcceptClientChanges(message.Values);
                        RewardShareCompatibility.AcceptRelease(envelope.PeerId, message.Values);
                        if (message.Values.TryGetValue("culture-edit", out string profile))
                            CultureCompatibility.Accept(envelope.PeerId, profile, true);
                    }
                }
                return;
            }
            if (!CoopSession.IsClient || message.Kind != 2 || string.IsNullOrEmpty(message.Session))
                return;
            if (_hostSession != message.Session)
            {
                _hostSession = message.Session;
                _lastRevision = 0;
            }
            if (message.Revision <= _lastRevision)
                return;
            _lastRevision = message.Revision;
            bool hadHostState = HasHostState;
            Applying = true;
            try
            {
                HostSettingsCompatibility.Apply(message.Values);
                if (message.Values.TryGetValue("character", out string character))
                    CultureCompatibility.LocalCharacterId = character;
                if (message.Values.TryGetValue("clan", out string clan))
                    HostClan = clan;
                if (message.Values.TryGetValue("companions", out string companions))
                    HostCompanions = int.Parse(companions, CultureInfo.InvariantCulture);
                if (message.Values.TryGetValue("workshops", out string workshops))
                    HostWorkshops = int.Parse(workshops, CultureInfo.InvariantCulture);
                if (message.Values.TryGetValue("income", out string income))
                    ClanIncomeCompatibility.HostIncome = float.Parse(income, CultureInfo.InvariantCulture);
                if (message.Values.TryGetValue("income-index", out string incomeIndex))
                    ClanIncomeCompatibility.ShareIndex = int.Parse(incomeIndex, CultureInfo.InvariantCulture);
                if (message.Values.TryGetValue("garrison-expense", out string garrisonExpense))
                    ClanIncomeCompatibility.HostGarrisonExpense = float.Parse(garrisonExpense, CultureInfo.InvariantCulture);
                if (message.Values.TryGetValue("players", out string players))
                    ClanIncomeCompatibility.PlayerCount = int.Parse(players, CultureInfo.InvariantCulture);
                if (message.Values.TryGetValue("speed", out string speedText))
                {
                    float speed = float.Parse(speedText, CultureInfo.InvariantCulture);
                    if (speed >= 1f && speed <= 128f)
                    {
                        bool showExtra = HasHostState && Math.Abs(HostSpeed - speed) > 0.001f && SpeedCompatibility.IsExtra(speed);
                        HostSpeed = speed;
                        Campaign.Current.SpeedUpMultiplier = speed;
                        if (showExtra)
                            SpeedCompatibility.Show(speed);
                    }
                }
                SmithingCompatibility.Apply(message.Values);
                AppearanceCompatibility.Apply(message.Values);
                GarrisonsCompatibility.Apply(message.Values);
                CultureCompatibility.Apply(message.Values);
                RewardShareCompatibility.Apply(message.Values);
                HasHostState = true;
            }
            finally { Applying = false; }
            if (!hadHostState && HasHostState)
                SmithingCompatibility.PublishClientRecords();
        }

        private Dictionary<string, string> Capture(bool settings = true)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (settings)
                HostSettingsCompatibility.Capture(values);
            values["clan"] = Clan.PlayerClan?.StringId ?? "";
            values["companions"] = (Clan.PlayerClan == null ? 0 :
                Campaign.Current.Models.ClanTierModel.GetCompanionLimit(Clan.PlayerClan)).ToString(CultureInfo.InvariantCulture);
            values["workshops"] = Campaign.Current.Models.WorkshopModel.MaximumWorkshopsPlayerCanHave.ToString(CultureInfo.InvariantCulture);
            values["speed"] = Campaign.Current.SpeedUpMultiplier.ToString("R", CultureInfo.InvariantCulture);
            ClanIncomeCompatibility.PlayerCount = 1 + _peers.Count;
            values["players"] = ClanIncomeCompatibility.PlayerCount.ToString(CultureInfo.InvariantCulture);
            if (Clan.PlayerClan != null)
            {
                if (_financeDirty)
                {
                    ClanIncomeCompatibility.RefreshPreview();
                    _financeDirty = false;
                }
                values["income"] = ClanIncomeCompatibility.HostIncome.ToString("R", CultureInfo.InvariantCulture);
                values["garrison-expense"] = ClanIncomeCompatibility.HostGarrisonExpense.ToString("R", CultureInfo.InvariantCulture);
            }
            return values;
        }

        private void Publish()
        {
            var publishClock = System.Diagnostics.Stopwatch.StartNew();
            foreach (int peer in _peers.Keys.Except(CoopSession.Server.GameplayPeerIds).ToArray())
            {
                _peers.Remove(peer);
                _peerCharacters.Remove(peer);
                _sentState.Remove(peer);
                _fullSync.Remove(peer);
            }
            ClanIncomeCompatibility.PlayerCount = 1 + _peers.Count;
            if (_peers.Count == 0)
                return;
            bool fullRequested = _fullSync.Count > 0;
            Dictionary<string, string> state = Capture(_settingsDirty || fullRequested);
            if (_settingsDirty || fullRequested)
            {
                GarrisonsCompatibility.Capture(state);
                SmithingCompatibility.CaptureFlags(state);
            }
            CultureCompatibility.Capture(state, _peers.Keys);
            RewardShareCompatibility.Capture(state, _peers.Keys);
            foreach (int peer in _peers.Keys)
            {
                bool full = _fullSync.Contains(peer);
                if (full)
                {
                    _sentState.Remove(peer);
                    _fullSync.Remove(peer);
                    SmithingCompatibility.Capture(state);
                }
                var personal = new Dictionary<string, string>(state);
                personal["income-index"] = (Array.IndexOf(_peers.Keys.OrderBy(id => id).ToArray(), peer) + 1).ToString(CultureInfo.InvariantCulture);
                SendState(peer, personal);
                if (full)
                {
                    AppearanceCompatibility.SendFullState(peer);
                    BattleLog.Write("[COOP-COMPAT] explicit full state synchronized peer=" + peer);
                }
                if (full)
                    KingdomSyncBehavior.Instance?.SendFullSyncToPeerBypassBattleGate(peer, "crashguard-state-change");
            }
            _settingsDirty = _runtimeDirty = false;
            if (publishClock.ElapsedMilliseconds >= 20 && (DateTime.UtcNow - _lastSlowPublish).TotalSeconds >= 15)
            {
                _lastSlowPublish = DateTime.UtcNow;
                BattleLog.Write("[COMPAT-PERF] publish-ms=" + publishClock.ElapsedMilliseconds + " peers=" + _peers.Count);
            }
        }

        internal void SendState(int peer, Dictionary<string, string> values)
        {
            if (!_sentState.TryGetValue(peer, out CompatibilityStateCache cache))
                _sentState.Add(peer, cache = new CompatibilityStateCache());
            values = cache.Changed(values);
            var chunk = new CompatibilityMessage { Kind = 2, Session = _session, Revision = ++_revision };
            int size = 0;
            foreach (var item in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                int length = System.Text.Encoding.UTF8.GetByteCount(item.Key) +
                    System.Text.Encoding.UTF8.GetByteCount(item.Value) + 16;
                if ((size + length > 36000 || chunk.Values.Count >= 512) && chunk.Values.Count > 0)
                {
                    if (CoopSession.Server.SendToPeerReliableOrdered(peer, CompatibilityWire.Encode(chunk)))
                        cache.Commit(chunk.Values);
                    else
                        BattleLog.Write("[COOP-COMPAT] send deferred; use explicit Sync if state is missing peer=" + peer);
                    chunk = new CompatibilityMessage { Kind = 2, Session = _session, Revision = ++_revision };
                    size = 0;
                }
                chunk.Values.Add(item.Key, item.Value);
                size += length;
            }
            if (chunk.Values.Count > 0)
                if (CoopSession.Server.SendToPeerReliableOrdered(peer, CompatibilityWire.Encode(chunk)))
                    cache.Commit(chunk.Values);
                else
                    BattleLog.Write("[COOP-COMPAT] send deferred; use explicit Sync if state is missing peer=" + peer);
        }

        internal void RequestFullSync(int peer = -1)
        {
            if (CoopSession.IsHost)
            {
                if (peer >= 0)
                    _fullSync.Add(peer);
                else
                    _fullSync.UnionWith(_peers.Keys);
                _timer = 0;
                _financeDirty = true;
            }
            else if (CoopSession.IsClient)
                _requestFullSync = true;
        }

        internal void MarkSettingsChanged()
        {
            if (Applying || !CoopSession.IsHost)
                return;
            _settingsDirty = true;
            _settingsDebounce = 0.2f;
        }

        internal void MarkFinanceChanged()
        {
            _financeDirty = true;
            _runtimeDirty = true;
        }

        internal void MarkRuntimeChanged() => _runtimeDirty = true;

        internal void PublishChanges(Dictionary<string, string> values)
        {
            if (CoopSession.IsHost)
            {
                foreach (int peer in _peers.Keys)
                    SendState(peer, values);
            }
            else if (HasHostState && CoopSession.IsClient)
            {
                CoopSession.Client?.SendRaw(CompatibilityWire.Encode(new CompatibilityMessage { Kind = 4, Values = values }));
            }
        }

        internal void PublishBattleVisual(string json)
        {
            var message = new CompatibilityMessage { Kind = 7, Session = _session };
            message.Values["visual"] = json;
            byte[] bytes = CompatibilityWire.Encode(message);
            foreach (int peer in _peers.Keys)
                CoopSession.Server?.SendToPeerReliableOrderedBypassBattleGate(peer, bytes);
        }

        internal bool IsCompatiblePeer(int peer) => _peers.ContainsKey(peer) &&
            CoopSession.Server?.GameplayPeerIds.Contains(peer) == true;

        internal int[] CompatiblePeers() => _peers.Keys.Where(IsCompatiblePeer).ToArray();

        internal void RequestSpeed(float speed)
        {
            if (_requestTimer > 0 || !CoopSession.AllowClientTimeControl)
                return;
            _requestTimer = 0.5f;
            var request = new CompatibilityMessage { Kind = 3 };
            request.Values["speed"] = speed.ToString("R", CultureInfo.InvariantCulture);
            CoopSession.Client?.SendRaw(CompatibilityWire.Encode(request));
        }

        [HarmonyPatch(typeof(Campaign), "SpeedUpMultiplier", MethodType.Setter)]
        private static class SpeedPatch
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(ref float value, out float __state)
            {
                __state = Campaign.Current?.SpeedUpMultiplier ?? value;
                if (!CoopSession.IsClient || !HasHostState || Applying)
                    return;
                if (Math.Abs(value - HostSpeed) > 0.001f)
                    Instance?.RequestSpeed(value);
                value = HostSpeed;
            }

            private static void Postfix(Campaign __instance, float __state)
            {
                SpeedCompatibility.Changed(__state, __instance.SpeedUpMultiplier);
            }
        }
    }
}
