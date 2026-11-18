using System;
using System.Collections.Generic;

namespace BannerlordTogetherCrashGuard
{
    // A peer's cache is committed only after its ordered send was accepted.
    internal sealed class CompatibilityStateCache
    {
        private readonly Dictionary<string, string> _sent = new Dictionary<string, string>(StringComparer.Ordinal);

        internal Dictionary<string, string> Changed(Dictionary<string, string> state)
        {
            var changed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in state)
                if (!_sent.TryGetValue(item.Key, out string previous) || previous != item.Value)
                    changed.Add(item.Key, item.Value);
            return changed;
        }

        internal void Commit(Dictionary<string, string> accepted)
        {
            foreach (var item in accepted)
                _sent[item.Key] = item.Value;
        }
    }
}
