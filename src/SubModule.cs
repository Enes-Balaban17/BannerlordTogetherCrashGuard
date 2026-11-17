using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BannerlordTogether;
using HarmonyLib;
using SandBox;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.MountAndBlade;

namespace BannerlordTogetherCrashGuard
{
    public sealed class SubModule : MBSubModuleBase
    {
        internal const string Owner = "bannerlord.together.crashguard";
        private Harmony _harmony;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            try
            {
                CheckBuild(typeof(Mission).Assembly,
                    "19387F31557FF840D14F378F6BBDF1D58FCFF406AB9FF2294DFBB3E49A50B87E");
                CheckBuild(typeof(MapEvent).Assembly,
                    "1F8E33E2ED73E6EC653D7629180AFB70649DDC6E5BD1657A802A264EFDA1C3AE");
                CheckBuild(typeof(SandBoxMissions).Assembly,
                    "587C6E94C425745C1478F632F3B66226FF8298666CB27E8E6D9A62488DF5956A");
                CheckBuild(typeof(CoopSession).Assembly,
                    "BB1D1998CF78D22C6C4CCABE3DD6D9A9E10BC8D3A5FC01B28DCE1C90CE4508DB");

                RaidUpdatePatch.DiagnosticsOnly = File.Exists(Path.Combine(BattleLog.Directory, "diagnostics-only"));
                _harmony = new Harmony(Owner);
                _harmony.PatchAll(typeof(SubModule).Assembly);
                BattleLog.Write($"Loaded v{typeof(SubModule).Assembly.GetName().Version.ToString(3)} " +
                    $"diagnosticsOnly={RaidUpdatePatch.DiagnosticsOnly}");
            }
            catch (Exception exception)
            {
                _harmony?.UnpatchAll(Owner);
                _harmony = null;
                BattleLog.Write($"Inactive: {exception.GetType().Name}: {exception.Message}");
            }
        }

        protected override void OnSubModuleUnloaded()
        {
            _harmony?.UnpatchAll(Owner);
            base.OnSubModuleUnloaded();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            if (_harmony == null)
                return;

            try
            {
                ShoulderCameraCompatibility.Apply(_harmony);
            }
            catch (Exception exception) when (DeploymentCrashGuard.CanSuppress(exception))
            {
                BattleLog.Write($"[SHOULDER-CAMERA] Optional hook unavailable: {exception.Message}");
            }
        }

        private static void CheckBuild(Assembly assembly, string expected)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(assembly.Location))
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Unsupported {assembly.GetName().Name} SHA-256 {actual}");
            }
        }
    }
}
