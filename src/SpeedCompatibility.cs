using System;
using System.Globalization;
using BannerlordTogether;
using HarmonyLib;
using TaleWorlds.Library;

namespace BannerlordTogetherCrashGuard
{
    internal static class SpeedCompatibility
    {
        private static bool _handling;

        internal static bool IsExtra(float speed)
        {
            Type type = AccessTools.TypeByName("BetterTime.Settings");
            object settings = type?.GetProperty("Instance", System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.FlattenHierarchy)?.GetValue(null);
            object configured = settings == null ? null : AccessTools.Property(settings.GetType(), "ExtraFastForwardMultiplier")?.GetValue(settings);
            float extra = configured == null ? 8f : Convert.ToSingle(configured, CultureInfo.InvariantCulture);
            return Math.Abs(speed - extra) < 0.001f;
        }

        internal static void Changed(float before, float after)
        {
            if (_handling || CoopCompatibilityBehavior.Applying || !CoopSession.IsHost || !CoopSession.IsSessionReady || Math.Abs(before - after) < 0.001f)
                return;
            try
            {
                _handling = true;
                if (IsExtra(after) && CoopSubModule.TryApplyHostTimeControlCommand(2, "Crash Guard BetterTime", false, false))
                    Show(after);
                CoopCompatibilityBehavior.Instance?.PublishChanges(new System.Collections.Generic.Dictionary<string, string>
                    { ["speed"] = after.ToString("R", CultureInfo.InvariantCulture) });
            }
            finally { _handling = false; }
        }

        internal static void Show(float speed)
        {
            InformationManager.DisplayMessage(new InformationMessage("[BT] Speed: " + speed.ToString("0.##", CultureInfo.InvariantCulture) + "x (Extra Fast)"));
        }
    }
}
