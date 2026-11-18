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
        private float _kingdomTimer;
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
            while (CompatibilityWire.TryReceive(out var incoming, out var envelope))
            {
                if (!ReferenceEquals(incoming, network))
                    continue;
                try { Receive(envelope); }
                catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
                { Report("receive", exception); }
            }
            RewardShareCompatibility.Tick();
            if (_requestFullSync && CoopSession.IsClient && HasHostState)
            {
                CoopSession.Client?.SendRaw(CompatibilityWire.Encode(new CompatibilityMessage { Kind = 5 }));
                _requestFullSync = false;
            }
            _timer -= dt;
            _kingdomTimer -= dt;
            if (_timer > 0f)
                return;
            _timer = 2f;
            try
            {
                AppearanceCompatibility.ObserveLocalHero();
                if (CoopSession.IsHost && CoopSession.Server != null)
                    Publish();
                else if (CoopSession.IsClient && !CoopSession.IsSpectator)
                {
                    if (!HasHostState)
                    {
                        var hello = new CompatibilityMessage { Kind = 1 };
                        hello.Values["culture"] = CultureCompatibility.LocalProfile();
                        CoopSession.Client?.SendRaw(CompatibilityWire.Encode(hello));
                    }
                    SmithingCompatibility.PublishClientRecords();
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
            _timer = 0;
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
                    CultureCompatibility.Capture(welcome, _peers.Keys);
                    SmithingCompatibility.Capture(welcome);
                    SendState(envelope.PeerId, welcome);
                    AppearanceCompatibility.SendFullState(envelope.PeerId);
                    _fullSync.Add(envelope.PeerId);
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
                if (message.Values.TryGetValue("players", out string players))
                    ClanIncomeCompatibility.PlayerCount = int.Parse(players, CultureInfo.InvariantCulture);
                if (message.Values.TryGetValue("speed", out string speedText))
                {
                    float speed = float.Parse(speedText, CultureInfo.InvariantCulture);
                    if (speed >= 1f && speed <= 128f)
                    {
                        HostSpeed = speed;
                        Campaign.Current.SpeedUpMultiplier = speed;
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
        }

        private Dictionary<string, string> Capture()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
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
                float income = Campaign.Current.Models.ClanFinanceModel.CalculateClanIncome(Clan.PlayerClan, false, false, false).ResultNumber;
                values["income"] = income.ToString("R", CultureInfo.InvariantCulture);
            }
            return values;
        }

        private void Publish()
        {
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
            Dictionary<string, string> state = Capture();
            GarrisonsCompatibility.Capture(state);
            SmithingCompatibility.Capture(state);
            AppearanceCompatibility.CaptureChanges(state);
            CultureCompatibility.Capture(state, _peers.Keys);
            RewardShareCompatibility.Capture(state, _peers.Keys);
            foreach (int peer in _peers.Keys)
            {
                bool full = _fullSync.Contains(peer);
                if (full)
                {
                    _sentState.Remove(peer);
                    _fullSync.Remove(peer);
                }
                SendState(peer, state);
                if (full)
                {
                    AppearanceCompatibility.SendFullState(peer);
                    BattleLog.Write("[COOP-COMPAT] explicit full state synchronized peer=" + peer);
                }
                if (_kingdomTimer <= 0f)
                    KingdomSyncBehavior.Instance?.SendFullSyncToPeerBypassBattleGate(peer, "crashguard-periodic-reconciliation");
            }
            if (_kingdomTimer <= 0f)
                _kingdomTimer = 10f;
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
                        _fullSync.Add(peer);
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
                    _fullSync.Add(peer);
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
            }
            else if (CoopSession.IsClient)
                _requestFullSync = true;
        }

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
            private static void Prefix(ref float value)
            {
                if (!CoopSession.IsClient || !HasHostState || Applying)
                    return;
                if (Math.Abs(value - HostSpeed) > 0.001f)
                    Instance?.RequestSpeed(value);
                value = HostSpeed;
            }
        }
    }
}
