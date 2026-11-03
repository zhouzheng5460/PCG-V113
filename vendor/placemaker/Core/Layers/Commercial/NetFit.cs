// s50 — the game folds an edge whose node geometry is too tight for the road widths (InvalidShape, RESEARCH-NOTES-ADDENDUM 32):
// each edge end is cut back at a node to where its edge crosses its neighbour's, (h_other + h_self · cos φ) / sin φ (a
// continuation needs none), and the two cut-backs of one edge must fit its length. MallNetGeometryTests checks the plans with
// this model; the lot planner uses it to choose among its variants (a lot whose connector turns too sharply is not kept).

using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Commercial
{
    public static class NetFit
    {
        /// <summary>Half width (m) of a planned net's asset, as the planners know it.</summary>
        public static float Half(PlannedNet n)
        {
            string id = n.Asset.Id ?? "";
            if (n.Role == NetRole.Pathway || id.Contains("Pedestrian Path")) return 1f;
            if (id.StartsWith("Alley")) return 4f;
            if (id.Contains("2xTwoway")) return id.Contains("Car Path") ? 3f : 3.5f;
            if (id.Contains("1xTwoway")) return id.Contains("Car Path") ? 1.5f : 2f;
            if (id.Contains("1xOneway")) return 2f;
            if (id.StartsWith("Pedestrian Street")) return id.EndsWith("Small") ? 3f : 4f;
            return 3f;
        }

        /// <summary>Cut-back along ray i caused by ray j at a node (rays as unit directions away from the node).</summary>
        public static float Cutback(float2 di, float hi, float2 dj, float hj)
        {
            float cos = math.clamp(math.dot(di, dj), -1f, 1f);
            if (cos < -0.9659f) return 0f;                                   // ≥ 165°: a continuation
            float sin = math.sqrt(math.max(1e-6f, 1f - cos * cos));
            return math.max(0f, (hj + hi * cos) / sin);
        }

        /// <summary>Alley / street / connector pieces (of these nets, joined at shared end points or on streets of the site) shorter than their cut-backs.</summary>
        public static List<string> Violations(IReadOnlyList<PlannedNet> nets, SiteContext site)
        {
            var all = new List<PlannedNet>();
            foreach (var n in nets) if (n != null && (n.Role != NetRole.Fence && n.Role != NetRole.Pathway || n.Asset.Id != null && n.Asset.Id.Contains("Path"))) all.Add(n);
            var bad = new List<string>();
            float Cut(PlannedNet n, bool start)
            {
                var p = start ? n.A : n.D; var q = start ? n.B : n.C;
                var di = math.normalizesafe((q - p).xz); float hi = Half(n); float cut = 0f;
                foreach (var m in all)
                {
                    if (ReferenceEquals(m, n)) continue;
                    foreach (bool ms in new[] { true, false })
                    {
                        var mp = ms ? m.A : m.D; var mq = ms ? m.B : m.C;
                        if (math.distance(p.xz, mp.xz) > 0.06f) continue;
                        cut = math.max(cut, Cutback(di, hi, math.normalizesafe((mq - mp).xz), Half(m)));
                    }
                }
                long id = start ? n.StartAttach : n.EndAttach;
                if (id >= 0 && site?.NearbyNets != null)
                    foreach (var s in site.NearbyNets)
                        if (s.ExternalId == id)
                        {
                            float t = start ? n.StartAttachT : n.EndAttachT;
                            var a = Composer.Bezier(s.A, s.B, s.C, s.D, math.saturate(t - 0.01f)).xz; var b = Composer.Bezier(s.A, s.B, s.C, s.D, math.saturate(t + 0.01f)).xz;
                            var tan = math.normalizesafe(b - a);
                            cut = math.max(cut, math.max(Cutback(di, hi, tan, s.Width * 0.5f), Cutback(di, hi, -tan, s.Width * 0.5f)));
                        }
                return cut;
            }
            foreach (var n in all)
            {
                if (n.Role != NetRole.Alley && n.Role != NetRole.Street && n.Role != NetRole.DriveConnector) continue;
                float len = math.distance(n.A.xz, n.D.xz);
                float need = Cut(n, true) + Cut(n, false);
                if (len < need - 0.05f) bad.Add($"{n.Role} '{n.Asset.Id}' ({n.A.x:0.0},{n.A.z:0.0})->({n.D.x:0.0},{n.D.z:0.0}) is {len:0.0} m, its node cut-backs take {need:0.0} m");
            }
            return bad;
        }
    }
}
