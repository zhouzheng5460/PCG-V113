// Per-plan object budget (s8, "ground before props"). A layer that fills ground with props (a crop
// plant every metre, a hedge piece every 50 cm, a pallet per square metre) produces thousands of
// objects: slow to build, heavy in game, and it hides that the ground should have been a surface
// (GroundCover). LayerBase runs this after every PlanCore:
//   cap = max(MinCap, perHectare × area ha). Above the cap the plan is THINNED — the biggest
//   repetitive groups first (water-filling: every "bulk" group, i.e. a category with ≥ BulkGroup
//   objects of a thinnable role, is cut down to one common level), never below (1 − MaxThin) of a
//   group. Decals are not objects here (s21): they take no space, have their own budget (Decals/Dressing)
//   and never count toward or get thinned by this cap. Thinnable roles: bushes, planters, furniture, trees. Never touched: benches,
//   lamps, bins, centerpieces, fence pieces, hedges / fences (rows must stay closed), functional
//   objects (parking bays, markers…) and small groups (a barn, a fountain). Thinning keeps an even
//   stride in plan order (rows stay rows, just sparser). The Review panel gets a Warning.

using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Common
{
    public static class ObjectBudget
    {
        public const float DefaultPerHectare = 650f;
        public const int MinCap = 300;
        public const int BulkGroup = 12;
        /// <summary>At most this share of one group is removed (the look must survive).</summary>
        public const float MaxThin = 0.75f;
        public const string IssueKey = "Placemaker.Common.Issue.ObjectBudget";

        public static int Cap(float areaM2, float perHectare) =>
            perHectare <= 0f ? int.MaxValue : math.max(MinCap, (int)math.round(areaM2 / 10000f * perHectare));

        private static bool Thinnable(PlannedObject o)
        {
            if (o == null) return false;
            switch (o.Role)
            {
                case ObjectRole.Bush: case ObjectRole.Planter: case ObjectRole.Furniture: case ObjectRole.Tree: break;
                default: return false;
            }
            var c = o.Category ?? "";
            return c != AssetCategories.Hedge && c != AssetCategories.Fence;
        }

        /// <summary>Thins the plan's objects down to the cap. Returns the number removed.</summary>
        public static int Enforce(AreaPlan plan, float areaM2, float perHectare)
        {
            if (plan?.Objects == null) return 0;
            int cap = Cap(areaM2, perHectare);
            int before = 0;
            foreach (var o in plan.Objects) if (o != null && o.Role != ObjectRole.Decal) before++;
            if (before <= cap) return 0;
            int excess = before - cap;

            var groups = new Dictionary<string, List<PlannedObject>>(System.StringComparer.Ordinal);
            foreach (var o in plan.Objects)
            {
                if (!Thinnable(o)) continue;
                var key = (int)o.Role + "|" + (o.Category ?? "");
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<PlannedObject>();
                l.Add(o);
            }
            var bulk = new List<List<PlannedObject>>();
            var keys = new List<string>(groups.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            foreach (var k in keys) if (groups[k].Count >= BulkGroup) bulk.Add(groups[k]);
            if (bulk.Count == 0) return 0;

            // Water level L: every group keeps max(L, floor) objects; the total removed covers the excess.
            int Floor(int n) => (int)math.ceil(n * (1f - MaxThin));
            int Removed(int level)
            {
                int r = 0;
                foreach (var g in bulk) r += math.max(0, g.Count - math.max(level, Floor(g.Count)));
                return r;
            }
            int lo = 0, hi = 0;
            foreach (var g in bulk) hi = math.max(hi, g.Count);
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Removed(mid) >= excess) lo = mid; else hi = mid - 1;
            }
            int levelKeep = lo;

            var drop = new HashSet<PlannedObject>();
            foreach (var g in bulk)
            {
                int keepN = math.min(g.Count, math.max(levelKeep, Floor(g.Count)));
                if (keepN >= g.Count) continue;
                float keep = (float)keepN / g.Count;
                // Even stride: keep element i when the running quota crosses an integer.
                for (int i = 0; i < g.Count; i++)
                    if ((int)math.floor((i + 1) * keep) == (int)math.floor(i * keep)) drop.Add(g[i]);
            }
            if (drop.Count == 0) return 0;
            plan.Objects.RemoveAll(o => drop.Contains(o));
            plan.Issues.Add(new PlanIssue
            {
                Severity = IssueSeverity.Warning,
                Key = IssueKey,
                Args = new object[] { before, cap, drop.Count },
            });
            plan.Facts["objectBudgetThinned"] = drop.Count;
            return drop.Count;
        }
    }
}
