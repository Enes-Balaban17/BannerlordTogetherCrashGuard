using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    internal static class ShoulderCameraCompatibility
    {
        private static int _reportedMissingMission;

        internal static void Apply(Harmony harmony)
        {
            Assembly xorberax = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "XorberaxLegacy");
            if (xorberax == null)
                return;

            MethodInfo target = FindTarget(xorberax);
            if (target == null)
            {
                BattleLog.Write("[SHOULDER-CAMERA] Optional Xorberax hook unavailable");
                return;
            }

            if (Harmony.GetPatchInfo(target)?.Owners.Contains(harmony.Id) == true)
                return;

            harmony.Patch(target, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(ShoulderCameraCompatibility), nameof(BeforeRangedCameraCheck))));
            BattleLog.Write($"[SHOULDER-CAMERA] Optional Xorberax hook active version={xorberax.GetName().Version}");
        }

        internal static MethodInfo FindTarget(Assembly assembly)
        {
            Type patch = assembly.GetType("XorberaxLegacy.Patches.ShoulderCameraPatch");
            return patch?.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).SingleOrDefault(method =>
                method.Name == "ShouldRevertCameraForRangedMode" && method.ReturnType == typeof(bool)
                && method.GetParameters().Select(parameter => parameter.ParameterType.FullName).SequenceEqual(
                    new[] { typeof(Agent).FullName, "TaleWorlds.MountAndBlade.View.Screens.MissionScreen" }));
        }

        private static bool BeforeRangedCameraCheck(ref bool __result)
        {
            if (RaidUpdatePatch.DiagnosticsOnly || !CoopSession.IsActive || Mission.Current != null)
                return true;

            // True asks Xorberax to use the normal camera for this frame.
            __result = true;
            if (Interlocked.Exchange(ref _reportedMissingMission, 1) == 0)
                BattleLog.Write("[SHOULDER-CAMERA] Used normal camera during co-op mission transition");
            return false;
        }
    }
}
