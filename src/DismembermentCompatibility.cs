using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Reflection.Emit;
using System.Web.Script.Serialization;
using BannerlordTogether;
using BannerlordTogether.Network;
using BannerlordTogether.SpNativeBattle;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal static class DismembermentCompatibility
    {
        public sealed class VisualEvent
        {
            public string Battle { get; set; }
            public string Id { get; set; }
            public ushort Victim { get; set; }
            public ushort Attacker { get; set; }
            public sbyte Bone { get; set; }
            public sbyte AttackBone { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
        }

        private static readonly ConcurrentQueue<Tuple<CoopNetworkBase, NetworkEnvelope>> Incoming =
            new ConcurrentQueue<Tuple<CoopNetworkBase, NetworkEnvelope>>();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly HashSet<string> Applied = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Tuple<VisualEvent, DateTime>> Pending =
            new Dictionary<string, Tuple<VisualEvent, DateTime>>();
        private static string _battle;
        private static bool _replaying;
        private static Type _logic;
        private static Type _eventType;

        internal static void Install(Harmony harmony)
        {
            _logic = AccessTools.TypeByName("DismembermentPlus.Logic.DismembermentPlusMissionLogic");
            _eventType = AccessTools.TypeByName("DismembermentPlus.Dismemberment.Dismemberment");
            if (_logic == null || _eventType == null)
                return;
            var dismember = AccessTools.Method(_logic, "Dismember");
            if (Harmony.GetPatchInfo(dismember)?.Owners.Contains(SubModule.Owner) == true)
                return;
            harmony.Patch(dismember, new HarmonyMethod(typeof(DismembermentCompatibility), nameof(BeforeDismember)),
                new HarmonyMethod(typeof(DismembermentCompatibility), nameof(AfterDismember)),
                new HarmonyMethod(typeof(DismembermentCompatibility), nameof(ReplaceFailureDialog)));
            harmony.Patch(AccessTools.Method(_logic, "CheckSetSlowMotion"),
                new HarmonyMethod(typeof(DismembermentCompatibility), nameof(SlowMotionPrefix)));
            BattleLog.Write("[DISMEMBERMENT] host visual events and co-op slow-motion guard active");
        }

        internal static bool IsVisual(byte[] bytes) => CompatibilityWire.IsOurs(bytes) && bytes.Length > 5 && bytes[5] == 7;

        internal static void Enqueue(CoopNetworkBase network, NetworkEnvelope envelope)
        {
            if (Incoming.Count < 128)
                Incoming.Enqueue(Tuple.Create(network, envelope));
        }

        private static bool SlowMotionPrefix() => !CoopSession.IsActive || !CoopSession.InSpNativeBattle;

        private static IEnumerable<CodeInstruction> ReplaceFailureDialog(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(typeof(DismembermentCompatibility), nameof(LogFailure));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
                    method.DeclaringType == typeof(System.Windows.Forms.MessageBox) && method.Name == "Show" &&
                    method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(string))
                    instruction.operand = replacement;
                yield return instruction;
            }
        }

        private static System.Windows.Forms.DialogResult LogFailure(string message)
        {
            if (!CoopSession.IsActive)
                return System.Windows.Forms.MessageBox.Show(message);
            BattleLog.Write("[DISMEMBERMENT] visual failure " + message);
            return System.Windows.Forms.DialogResult.None;
        }

        private static bool BeforeDismember(object dismemberment)
        {
            if (!CoopSession.IsActive || !CoopSession.InSpNativeBattle)
                return true;
            if (CoopSession.IsClient && CoopCompatibilityBehavior.HasHostState && !_replaying)
                return false;
            Agent victim = AccessTools.Property(dismemberment.GetType(), "Victim").GetValue(dismemberment) as Agent;
            return victim?.AgentVisuals != null && victim.AgentVisuals.GetSkeleton() != null;
        }

        private static void AfterDismember(object dismemberment)
        {
            if (!CoopSession.IsHost || !CoopSession.InSpNativeBattle || _replaying)
                return;
            Type type = dismemberment.GetType();
            Agent victim = (Agent)AccessTools.Property(type, "Victim").GetValue(dismemberment);
            Agent attacker = (Agent)AccessTools.Property(type, "Attacker").GetValue(dismemberment);
            if (victim?.AgentVisuals == null || attacker == null || victim.Index < 0 || victim.Index > ushort.MaxValue)
                return;
            var collision = (AttackCollisionData)AccessTools.Property(type, "CollisionData").GetValue(dismemberment);
            var visual = new VisualEvent
            {
                Battle = CoopSession.SpNativeBattleId, Id = Guid.NewGuid().ToString("N"),
                Victim = (ushort)victim.Index, Attacker = (ushort)attacker.Index,
                Bone = collision.CollisionBoneIndex, AttackBone = collision.AttackBoneIndex,
                X = collision.WeaponBlowDir.x, Y = collision.WeaponBlowDir.y, Z = collision.WeaponBlowDir.z
            };
            CoopCompatibilityBehavior.Instance?.PublishBattleVisual(Json.Serialize(visual));
        }

        internal static void Tick()
        {
            string battle = CoopSession.SpNativeBattleId;
            if (_battle != battle)
            {
                _battle = battle;
                Pending.Clear();
                Applied.Clear();
            }
            while (Incoming.TryDequeue(out var item))
            {
                if (_logic == null || !CoopSession.IsClient || !ReferenceEquals(item.Item1, CoopSession.Client) ||
                    !CoopSession.InSpNativeBattle || Mission.Current == null)
                    continue;
                try
                {
                    CompatibilityMessage message = CompatibilityWire.Decode(item.Item2.Data);
                    if (!message.Values.TryGetValue("visual", out string json))
                        continue;
                    VisualEvent visual = Json.Deserialize<VisualEvent>(json);
                    if (visual?.Battle == battle && !string.IsNullOrEmpty(visual.Id) && !Applied.Contains(visual.Id) && Pending.Count < 128)
                        Pending[visual.Id] = Tuple.Create(visual, DateTime.UtcNow);
                }
                catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
                { BattleLog.Write("[DISMEMBERMENT] invalid visual event " + exception.Message); }
            }
            foreach (var item in new List<KeyValuePair<string, Tuple<VisualEvent, DateTime>>>(Pending))
            {
                VisualEvent visual = item.Value.Item1;
                Agent victim = SpNativeBattleClientMissionBehavior.Instance?.GetAgentByHostIndex(visual.Victim);
                if (victim?.AgentVisuals != null && (victim.State == AgentState.Killed || victim.State == AgentState.Unconscious))
                {
                    try { Replay(visual, victim); }
                    catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
                    { BattleLog.Write("[DISMEMBERMENT] event replay unavailable " + exception.Message); }
                    Applied.Add(visual.Id);
                    Pending.Remove(item.Key);
                }
                else if (DateTime.UtcNow - item.Value.Item2 > TimeSpan.FromSeconds(3))
                    Pending.Remove(item.Key);
            }
        }

        private static void Replay(VisualEvent visual, Agent victim)
        {
            object logic = Mission.Current.MissionBehaviors.Find(behavior => _logic.IsInstanceOfType(behavior));
            if (logic == null)
                return;
            if (visual.Bone < 0 || visual.Bone >= victim.AgentVisuals.GetSkeleton().GetBoneCount())
                return;
            object data = Activator.CreateInstance(_eventType);
            object collision = default(AttackCollisionData);
            AccessTools.Property(typeof(AttackCollisionData), "CollisionBoneIndex").SetValue(collision, visual.Bone);
            AccessTools.Property(typeof(AttackCollisionData), "AttackBoneIndex").SetValue(collision, visual.AttackBone);
            AccessTools.Field(typeof(AttackCollisionData), "_weaponBlowDir").SetValue(collision, new Vec3(visual.X, visual.Y, visual.Z));
            AccessTools.Property(_eventType, "Victim").SetValue(data, victim);
            AccessTools.Property(_eventType, "Attacker").SetValue(data,
                SpNativeBattleClientMissionBehavior.Instance?.GetAgentByHostIndex(visual.Attacker) ?? victim);
            AccessTools.Property(_eventType, "CollisionData").SetValue(data, collision);
            AccessTools.Property(_eventType, "Id").SetValue(data, Guid.Parse(visual.Id));
            try
            {
                _replaying = true;
                AccessTools.Method(_logic, "Dismember").Invoke(logic, new[] { data });
            }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            { BattleLog.Write("[DISMEMBERMENT] visual replay failed " + exception.Message); }
            finally { _replaying = false; }
        }
    }
}
