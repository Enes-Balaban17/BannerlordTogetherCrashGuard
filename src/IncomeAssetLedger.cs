using System;
using System.Collections.Generic;

namespace BannerlordTogetherCrashGuard
{
    internal sealed class IncomeAssetLedger
    {
        private readonly Dictionary<string, Func<bool, int>> _assets = new Dictionary<string, Func<bool, int>>(StringComparer.Ordinal);

        internal void Add(string id, Func<bool, int> collect)
        {
            if (!_assets.ContainsKey(id))
                _assets.Add(id, collect);
        }

        internal int Collect(bool withdraw)
        {
            int total = 0;
            foreach (var collect in _assets.Values)
                total = checked(total + collect(withdraw));
            return total;
        }
    }
}
