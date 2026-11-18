using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;
using BannerlordTogether;
using BannerlordTogether.Network;
using BannerlordTogether.SpNativeBattle;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace BannerlordTogetherCrashGuard
{
    internal static class RewardShareCompatibility
    {
        private sealed class Context
        {
            internal Context Previous;
            internal MapEvent Battle;
            internal int[] Peers;
            internal Dictionary<EquipmentElement, int> Items;
            internal Dictionary<CharacterObject, TroopRosterElement> Prisoners;
            internal Dictionary<CharacterObject, TroopRosterElement> Members;
            internal int Gold;
            internal bool Committed;
            internal string RewardBattleId;
        }

        public sealed class PendingReward
        {
            public string Character { get; set; }
            public BattleRewardPacket Packet { get; set; }
        }

        [ThreadStatic] private static Context _context;
        private static readonly ConditionalWeakTable<MapEvent, Context> Contexts = new ConditionalWeakTable<MapEvent, Context>();
        private static readonly Dictionary<string, string> Lords = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Actors = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PendingReward> Pending = new Dictionary<string, PendingReward>(StringComparer.Ordinal);
        private static readonly HashSet<string> AppliedLords = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReleasePending = new HashSet<string>(StringComparer.Ordinal);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal static void Install(Harmony harmony)
        {
            var calculate = AccessTools.Method(typeof(MapEvent), "CalculateAndCommitMapEventResults");
            if (Harmony.GetPatchInfo(calculate)?.Owners.Contains(SubModule.Owner) == true)
                return;
            var prefix = new HarmonyMethod(typeof(RewardShareCompatibility), nameof(BeforeResults))
                { priority = Priority.Last, after = new[] { "BannerlordTogether.mod" } };
            harmony.Patch(calculate, prefix, finalizer: new HarmonyMethod(typeof(RewardShareCompatibility), nameof(AfterResults)));
            harmony.Patch(AccessTools.Method(typeof(CoopServer), "BroadcastRawReliable"),
                new HarmonyMethod(typeof(RewardShareCompatibility), nameof(BeforeRewardBroadcast)));
            harmony.Patch(AccessTools.Method(typeof(BattleSyncBehavior), "SanitizeGhostAfterMergedBattle"),
                transpiler: new HarmonyMethod(typeof(RewardShareCompatibility), nameof(PreserveAssignedLords)));
            Type nativeRewards = typeof(CoopSession).Assembly.GetType("G.GM", true);
            harmony.Patch(AccessTools.Method(nativeRewards, "C", new[] { typeof(BattleRewardPacket) }),
                new HarmonyMethod(typeof(RewardShareCompatibility), nameof(RewardEmptyPrefix)));
        }

        private static Dictionary<CharacterObject, TroopRosterElement> Snapshot(TroopRoster roster)
        {
            return roster.GetTroopRoster().Where(row => row.Character != null)
                .ToDictionary(row => row.Character, row => row);
        }

        private static void BeforeResults(MapEvent __instance, out Context __state)
        {
            __state = null;
            try { PrepareResults(__instance, out __state); }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            { BattleLog.Write("[LOOT-SHARE] could not capture reward baseline " + exception); }
        }

        private static void PrepareResults(MapEvent __instance, out Context __state)
        {
            __state = null;
            if (!CoopSession.IsGameplayHost || CoopSession.InFriendlyPvpDuel || !__instance.HasWinner ||
                __instance.PlayerSide != __instance.WinningSide || PlayerEncounter.Current == null)
                return;
            Type rewardType = typeof(CoopSession).Assembly.GetType("G.GM", true);
            var snapshots = AccessTools.Field(rewardType, "aeu").GetValue(null) as IDictionary;
            object snapshot = snapshots?[__instance];
            if (snapshot == null)
                return;
            int[] peers = (int[])AccessTools.Field(snapshot.GetType(), "btK").GetValue(snapshot);
            if (peers == null || peers.Length == 0 || peers.Any(peer =>
                CoopCompatibilityBehavior.Instance?.IsCompatiblePeer(peer) != true ||
                string.IsNullOrEmpty(CoopPeerIdentity.CharacterId(peer)) || CoopPeerIdentity.HeroForPeer(peer)?.PartyBelongedTo == null))
                return;
            Context context = Contexts.GetValue(__instance, _ => new Context
            {
                Battle = __instance, Peers = peers.Distinct().OrderBy(peer => peer).ToArray(),
                Items = PlayerEncounter.Current.RosterToReceiveLootItems.ToDictionary(row => row.EquipmentElement, row => row.Amount),
                Prisoners = Snapshot(PlayerEncounter.Current.RosterToReceiveLootPrisoners),
                Members = Snapshot(PlayerEncounter.Current.RosterToReceiveLootMembers), Gold = Hero.MainHero.Gold
            });
            context.Previous = _context;
            _context = context;
            __state = context;
        }

        private static void AfterResults(Context __state)
        {
            if (__state != null)
                _context = __state.Previous;
        }

        internal static int[] SplitCount(int total, int participants, int start)
        {
            if (total < 0 || participants < 1 || start < 0 || start >= participants)
                throw new ArgumentOutOfRangeException();
            var shares = Enumerable.Repeat(total / participants, participants).ToArray();
            for (int index = 0; index < total % participants; index++)
                shares[(start + index) % participants]++;
            return shares;
        }

        private static bool RewardEmptyPrefix(BattleRewardPacket __0, ref bool __result)
        {
            if (_context == null || _context.Committed || __0.Source != (byte)BattleRewardSource.PartySizeShare || PlayerEncounter.Current == null)
                return true;
            bool hasPool = PlayerEncounter.Current.RosterToReceiveLootItems.Any(row =>
                row.Amount > (_context.Items.TryGetValue(row.EquipmentElement, out int before) ? before : 0)) ||
                PlayerEncounter.Current.RosterToReceiveLootPrisoners.GetTroopRoster().Any(row =>
                    row.Number > (_context.Prisoners.TryGetValue(row.Character, out var before) ? before.Number : 0));
            if (!hasPool && Hero.MainHero.Gold <= _context.Gold)
                return true;
            __result = false;
            return false;
        }

        private static bool BeforeRewardBroadcast(byte[] data)
        {
            try { return RebalanceReward(data); }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            {
                BattleLog.Write("[LOOT-SHARE] allocation failed committed=" + (_context?.Committed == true) + " " + exception);
                return _context?.Committed != true;
            }
        }

        private static bool RebalanceReward(byte[] data)
        {
            Context context = _context;
            if (context == null || data == null || data.Length == 0 ||
                data[0] != (byte)PacketType.SpNativeBattleReward)
                return true;
            BattleRewardPacket original = PacketSerializer.DeserializeSpNativeBattleReward(data);
            if (original.Source != (byte)BattleRewardSource.PartySizeShare || PlayerEncounter.Current == null)
                return true;
            if (context.Committed)
                return context.RewardBattleId != original.BattleId;
            int count = context.Peers.Length + 1;
            var packets = new BattleRewardPacket[context.Peers.Length];
            var items = packets.Select(_ => new List<BattleRewardItemEntry>()).ToArray();
            var prisoners = packets.Select(_ => new List<BattleRewardTroopEntry>()).ToArray();
            var members = packets.Select(_ => new List<BattleRewardTroopEntry>()).ToArray();
            var itemDebits = new List<Tuple<EquipmentElement, int>>();
            var prisonerDebits = new List<Tuple<CharacterObject, int, int>>();
            var memberDebits = new List<Tuple<CharacterObject, int, int>>();
            var lordTransfers = new List<Tuple<Hero, int>>();
            int cursor = 0;
            foreach (ItemRosterElement row in PlayerEncounter.Current.RosterToReceiveLootItems.ToList())
            {
                context.Items.TryGetValue(row.EquipmentElement, out int before);
                int gained = Math.Max(0, row.Amount - before);
                if (gained == 0 || row.EquipmentElement.Item == null)
                    continue;
                int[] shares = SplitCount(gained, count, cursor);
                cursor = (cursor + gained % count) % count;
                for (int index = 1; index < count; index++)
                    if (shares[index] > 0)
                        items[index - 1].Add(new BattleRewardItemEntry
                        {
                            ItemStringId = row.EquipmentElement.Item.StringId,
                            ItemModifierId = row.EquipmentElement.ItemModifier?.StringId ?? "",
                            Count = checked((ushort)shares[index])
                        });
                itemDebits.Add(Tuple.Create(row.EquipmentElement, gained - shares[0]));
            }
            PlanTroops(PlayerEncounter.Current.RosterToReceiveLootPrisoners, context.Prisoners, count,
                ref cursor, prisoners, prisonerDebits, lordTransfers, context.Peers);
            PlanTroops(PlayerEncounter.Current.RosterToReceiveLootMembers, context.Members, count,
                ref cursor, members, memberDebits, null, context.Peers);
            int goldGain = Math.Max(0, Hero.MainHero.Gold - context.Gold);
            int[] gold = SplitCount(goldGain, count, cursor);
            // Finish every allocation and serialization before changing any roster or gold.
            for (int index = 0; index < packets.Length; index++)
            {
                packets[index] = original;
                packets[index].RecipientGameplaySourceKey = CoopPeerIdentity.SourceKey(context.Peers[index]);
                packets[index].GoldGain = gold[index + 1];
                packets[index].Items = items[index].ToArray();
                packets[index].PrisonerRewards = prisoners[index].ToArray();
                packets[index].MemberRewards = members[index].ToArray();
                packets[index].TroopXp = Array.Empty<BattleRewardXpEntry>();
                PacketSerializer.SerializeSpNativeBattleReward(packets[index]);
            }
            CommitAllocation(context, itemDebits, prisonerDebits, memberDebits, goldGain - gold[0], lordTransfers);
            context.Committed = true;
            context.RewardBattleId = original.BattleId;
            for (int index = 0; index < packets.Length; index++)
            {
                int peer = context.Peers[index];
                string character = CoopPeerIdentity.CharacterId(peer);
                string key = character + ":" + original.BattleId;
                Pending[key] = new PendingReward { Character = character, Packet = packets[index] };
                if (CoopSession.Server.SendToPeerReliableOrderedBypassBattleGate(peer, PacketSerializer.SerializeSpNativeBattleReward(packets[index])))
                    Pending.Remove(key);
            }
            BattleLog.Write("[LOOT-SHARE] conserved host reward pool battle=" + original.BattleId +
                " participants=" + count + " gold=" + goldGain + " uniqueLords=" + lordTransfers.Count);
            return false;
        }

        private static void CommitAllocation(Context context, List<Tuple<EquipmentElement, int>> items,
            List<Tuple<CharacterObject, int, int>> prisoners, List<Tuple<CharacterObject, int, int>> members,
            int gold, List<Tuple<Hero, int>> lords)
        {
            var itemRoster = PlayerEncounter.Current.RosterToReceiveLootItems;
            var prisonerRoster = PlayerEncounter.Current.RosterToReceiveLootPrisoners;
            var memberRoster = PlayerEncounter.Current.RosterToReceiveLootMembers;
            var oldItems = itemRoster.ToArray();
            var oldPrisoners = prisonerRoster.GetTroopRoster().ToArray();
            var oldMembers = memberRoster.GetTroopRoster().ToArray();
            int oldGold = Hero.MainHero.Gold;
            var oldCaptors = lords.ToDictionary(transfer => transfer.Item1, transfer => transfer.Item1.PartyBelongedToAsPrisoner);
            var oldOwners = new Dictionary<string, string>(Lords);
            try
            {
                foreach (var debit in items)
                    itemRoster.AddToCounts(debit.Item1, -debit.Item2);
                ApplyDebits(prisonerRoster, prisoners);
                ApplyDebits(memberRoster, members);
                Hero.MainHero.ChangeHeroGold(-gold);
                foreach (var transfer in lords)
                {
                    Hero hero = transfer.Item1;
                    MobileParty target = CoopPeerIdentity.HeroForPeer(transfer.Item2).PartyBelongedTo;
                    MovePrisoner(hero, target.Party);
                    Lords[hero.StringId] = CoopPeerIdentity.CharacterId(transfer.Item2);
                    BattleLog.Write("[LOOT-SHARE] lord=" + hero.StringId + " owner=" + Lords[hero.StringId]);
                }
            }
            catch
            {
                try
                {
                    foreach (var captor in oldCaptors)
                    {
                        if (captor.Value != null)
                            MovePrisoner(captor.Key, captor.Value);
                        else if (captor.Key.IsPrisoner)
                            EndCaptivityAction.ApplyByReleasedAfterBattle(captor.Key);
                    }
                    itemRoster.Clear();
                    foreach (var item in oldItems)
                        itemRoster.AddToCounts(item.EquipmentElement, item.Amount);
                    RestoreRoster(prisonerRoster, oldPrisoners);
                    RestoreRoster(memberRoster, oldMembers);
                    Hero.MainHero.ChangeHeroGold(oldGold - Hero.MainHero.Gold);
                    Lords.Clear();
                    foreach (var owner in oldOwners)
                        Lords.Add(owner.Key, owner.Value);
                }
                catch (Exception rollback) when (DeploymentCrashGuard.CanSuppress(rollback))
                {
                    context.Committed = true;
                    BattleLog.Write("[LOOT-SHARE] rollback failed, duplicate fallback blocked " + rollback);
                }
                throw;
            }
        }

        private static void RestoreRoster(TroopRoster target, TroopRosterElement[] rows)
        {
            target.Clear();
            foreach (var row in rows)
                target.AddToCounts(row.Character, row.Number, false, row.WoundedNumber, row.Xp);
        }

        private static void PlanTroops(TroopRoster roster, Dictionary<CharacterObject, TroopRosterElement> baseline, int count,
            ref int cursor, List<BattleRewardTroopEntry>[] rewards, List<Tuple<CharacterObject, int, int>> debits,
            List<Tuple<Hero, int>> lords, int[] peers)
        {
            foreach (TroopRosterElement row in roster.GetTroopRoster())
            {
                baseline.TryGetValue(row.Character, out var before);
                int gained = Math.Max(0, row.Number - before.Number);
                if (gained == 0)
                    continue;
                int[] shares = SplitCount(row.Character.IsHero ? 1 : gained, count, cursor);
                cursor = (cursor + gained % count) % count;
                if (row.Character.IsHero)
                {
                    if (lords == null || row.Character.HeroObject == Hero.MainHero || !row.Character.HeroObject.IsLord)
                        continue;
                    int recipient = Array.FindIndex(shares, amount => amount > 0);
                    if (recipient > 0)
                    {
                        debits.Add(Tuple.Create(row.Character, row.Number, row.WoundedNumber));
                        lords.Add(Tuple.Create(row.Character.HeroObject, peers[recipient - 1]));
                    }
                    else if (row.Number > 1)
                        debits.Add(Tuple.Create(row.Character, row.Number - 1, Math.Min(row.WoundedNumber, row.Number - 1)));
                    continue;
                }
                int wounded = Math.Min(gained, Math.Max(0, row.WoundedNumber - before.WoundedNumber));
                int remainingWounded = wounded;
                int remoteWounded = 0;
                for (int index = 1; index < count; index++)
                {
                    int shareWounded = Math.Min(remainingWounded, shares[index]);
                    remainingWounded -= shareWounded;
                    remoteWounded += shareWounded;
                    if (shares[index] > 0)
                        rewards[index - 1].Add(new BattleRewardTroopEntry
                        {
                            TroopStringId = row.Character.StringId, HealthyCount = checked((ushort)(shares[index] - shareWounded)),
                            WoundedCount = checked((ushort)shareWounded)
                        });
                }
                debits.Add(Tuple.Create(row.Character, gained - shares[0], remoteWounded));
            }
        }

        private static void ApplyDebits(TroopRoster roster, List<Tuple<CharacterObject, int, int>> debits)
        {
            foreach (var debit in debits)
                roster.AddToCounts(debit.Item1, -debit.Item2, false, -debit.Item3);
        }

        private static IEnumerable<CodeInstruction> PreserveAssignedLords(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(EndCaptivityAction), nameof(EndCaptivityAction.ApplyByReleasedAfterBattle));
            var replacement = AccessTools.Method(typeof(RewardShareCompatibility), nameof(ReleaseUnassignedPrisoner));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(original))
                    instruction.operand = replacement;
                yield return instruction;
            }
        }

        private static void ReleaseUnassignedPrisoner(Hero hero)
        {
            if (hero != null && Lords.TryGetValue(hero.StringId, out string owner) && !string.IsNullOrEmpty(owner))
                return;
            EndCaptivityAction.ApplyByReleasedAfterBattle(hero);
        }

        private static void MovePrisoner(Hero hero, PartyBase target)
        {
            if (hero.PartyBelongedToAsPrisoner == target)
                return;
            if (hero.IsPrisoner)
                EndCaptivityAction.ApplyByReleasedAfterBattle(hero);
            TakePrisonerAction.Apply(target, hero);
        }

        internal static void Capture(Dictionary<string, string> state, IEnumerable<int> peers)
        {
            // An escape, death, ransom or transfer ends our assignment. Never recapture a
            // freed lord from an old ledger entry when a client reconnects.
            foreach (string id in Lords.Keys.ToArray())
            {
                if (string.IsNullOrEmpty(Lords[id]))
                    continue;
                Hero hero = MBObjectManager.Instance.GetObject<Hero>(id);
                if (hero == null || hero.IsDead || !hero.IsPrisoner)
                    Lords[id] = "";
            }
            foreach (int peer in peers)
            {
                string character = CoopPeerIdentity.CharacterId(peer);
                state["actor/" + character] = CoopPeerIdentity.SourceKey(peer);
                foreach (var pending in Pending.Where(pair => pair.Value.Character == character).ToArray())
                    if (CoopSession.Server.SendToPeerReliableOrderedBypassBattleGate(peer,
                        PacketSerializer.SerializeSpNativeBattleReward(pending.Value.Packet)))
                        Pending.Remove(pending.Key);
            }
            foreach (var entry in Lords)
                state["lord/" + entry.Key] = entry.Value;
        }

        internal static void Apply(Dictionary<string, string> state)
        {
            foreach (var entry in state)
                if (entry.Key.StartsWith("actor/", StringComparison.Ordinal))
                    Actors[entry.Key.Substring(6)] = entry.Value;
                else if (entry.Key.StartsWith("lord/", StringComparison.Ordinal))
                    Lords[entry.Key.Substring(5)] = entry.Value;
        }

        internal static void Tick()
        {
            if (!CoopSession.IsClient || !CoopCompatibilityBehavior.HasHostState || PlayerEncounter.Current != null ||
                !(Game.Current?.GameStateManager.ActiveState is MapState))
                return;
            foreach (var entry in Lords)
            {
                Hero hero = MBObjectManager.Instance.GetObject<Hero>(entry.Key);
                if (hero == null || hero.IsDead)
                    continue;
                if (string.IsNullOrEmpty(entry.Value))
                {
                    if (AppliedLords.Remove(entry.Key) && hero.PartyBelongedToAsPrisoner == PartyBase.MainParty)
                        EndCaptivityAction.ApplyByReleasedAfterBattle(hero);
                    ReleasePending.Remove(entry.Key);
                    continue;
                }
                if (entry.Value == CultureCompatibility.LocalCharacterId)
                {
                    if (AppliedLords.Contains(entry.Key) && hero.PartyBelongedToAsPrisoner != PartyBase.MainParty)
                    {
                        if (ReleasePending.Add(entry.Key))
                            CoopCompatibilityBehavior.Instance.PublishChanges(new Dictionary<string, string> { ["lord-release/" + entry.Key] = "1" });
                        continue;
                    }
                    MovePrisoner(hero, PartyBase.MainParty);
                    AppliedLords.Add(entry.Key);
                }
                else if (Actors.TryGetValue(entry.Value, out string source))
                {
                    PartyBase target = CoopPeerIdentity.HeroForSource(source)?.PartyBelongedTo?.Party;
                    if (target != null)
                        MovePrisoner(hero, target);
                }
            }
        }

        internal static void AcceptRelease(int peer, Dictionary<string, string> state)
        {
            string character = CoopPeerIdentity.CharacterId(peer);
            foreach (var entry in state)
            {
                if (!entry.Key.StartsWith("lord-release/", StringComparison.Ordinal))
                    continue;
                string id = entry.Key.Substring(13);
                if (!Lords.TryGetValue(id, out string owner) || owner != character)
                    continue;
                Hero hero = MBObjectManager.Instance.GetObject<Hero>(id);
                if (hero?.IsPrisoner == true)
                    EndCaptivityAction.ApplyByReleasedAfterBattle(hero);
                Lords[id] = "";
            }
        }

        internal static void BeginCampaign()
        {
            Lords.Clear(); Actors.Clear(); Pending.Clear(); AppliedLords.Clear(); ReleasePending.Clear();
        }

        internal static void SyncData(IDataStore store)
        {
            string lords = store.IsSaving ? Json.Serialize(Lords) : "";
            string pending = store.IsSaving ? Json.Serialize(Pending) : "";
            store.SyncData("btcg_lord_custody_v1", ref lords);
            store.SyncData("btcg_pending_rewards_v1", ref pending);
            if (store.IsLoading)
            {
                Lords.Clear(); Pending.Clear();
                if (!string.IsNullOrEmpty(lords))
                    foreach (var item in Json.Deserialize<Dictionary<string, string>>(lords))
                        Lords[item.Key] = item.Value;
                if (!string.IsNullOrEmpty(pending))
                    foreach (var item in Json.Deserialize<Dictionary<string, PendingReward>>(pending))
                        Pending[item.Key] = item.Value;
            }
        }
    }
}
