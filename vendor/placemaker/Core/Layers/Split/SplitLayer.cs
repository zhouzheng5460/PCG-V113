// Split layer: divides its region into parts that children fill independently
// ("split park | plaza", "half parking, half park"…). Builds nothing except an optional path
// along each cut.
//
// Cuts come from the player's divider (EdgeMarkers of kind "split" on the outline, taken in
// pairs: markers 0–1 form the first cut, 2–3 the second…), or — without markers — from the
// ratio/angle controls: cut lines perpendicular to the long axis rotated by `angle`, placed so
// part:0 gets `ratio` % of the area (equal shares for 3–4 parts).
//
// Regions: part:N with tags {part, part:N}. Part areas always sum to the parent area because
// every cut is a chord split (see PolygonSplitter).

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Common;
using Unity.Mathematics;

namespace Placemaker.Core.Layers.Split
{
    [AreaGenerator(60, Hidden = true)]
    public sealed class SplitLayer : LayerBase
    {
        public const string LayerId = "split";
        public const string MarkerKind = "split";
        public const int MaxParts = 4;

        protected override string Area => "Split";
        public override string Id => LayerId;
        public override string Icon => "Media/Tools/Snap Options/ExistingGeometry.svg";
        public override LayerKind Kind => LayerKind.Split;
        public override string[] Emits => new[] { RegionTags.Part, "part:0", "part:1", "part:2", "part:3" };
        public override float MinArea => 100f;

        protected override void DefineControls(ControlSet c)
        {
            c.Markers("cuts", ControlTier.Essential, MarkerKind).Tip().In("layout");
            c.Slider("ratio", 50, 10, 90, 1, "%", ControlTier.Essential, "layout").Tip().Cheap();
            c.Slider("angle", 0, 0, 175, 5, "deg", ControlTier.Essential, "layout").Tip().Cheap();
            c.Slider("parts", 2, 2, MaxParts, 1, "count", ControlTier.More, "layout");
            c.Choice("divider", "none", ControlTier.More, "divider", "none", "path", "wide").Tip();
            c.Asset("dividerAsset", AssetCategories.Pathway, ControlTier.More, "divider");
            c.RandomRange("ratio", 30, 70);
            c.Fixed("parts");
            c.Fixed("divider");
        }

        protected override void DefinePresets()
        {
            Preset("halves", ("ratio", 50.0), ("angle", 0.0), ("parts", 2.0));
            Preset("thirds", ("parts", 3.0), ("angle", 0.0));
            Preset("lengthwise", ("ratio", 50.0), ("angle", 90.0), ("parts", 2.0));
            Preset("promenade", ("ratio", 50.0), ("angle", 0.0), ("divider", "wide"));
        }

        protected override void PlanCore(LayerContext ctx)
        {
            var region = ctx.LayerRegion;
            var parts = new List<float2[]> { region.Ring };
            var cuts = new List<Chord>();
            var markers = new List<EdgeMarker>();
            foreach (var m in ctx.Input.Markers) if (m.Kind == MarkerKind) markers.Add(m);
            bool ok;
            if (markers.Count >= 2)
            {
                ok = CutByMarkers(parts, cuts, markers);
                if (markers.Count % 2 == 1) ctx.Issue(IssueSeverity.Info, "Placemaker.Split.Issue.UnpairedMarker");
            }
            else ok = CutByRatio(ctx, region, parts, cuts);
            if (!ok) ctx.Issue(IssueSeverity.Warning, "Placemaker.Split.Issue.CannotSplit");
            ctx.Check();

            for (int i = 0; i < parts.Count; i++)
                ctx.AddRegion(parts[i], "part:" + i, RegionTags.Part, "part:" + i);

            string divider = ctx.S("divider");
            if (divider != "none")
            {
                var role = divider == "wide" ? NetRole.PathwayWide : NetRole.Pathway;
                var asset = AssetMix.One(ctx.S("dividerAsset"));
                foreach (var c in cuts) ctx.StraightNet(c.A, c.B, role, asset);
            }
            ctx.Stat(Keys.StatParts, parts.Count, Keys.UnitCount);
            ctx.Plan.Facts["parts"] = parts.Count;
        }

        private static bool CutByMarkers(List<float2[]> parts, List<Chord> cuts, List<EdgeMarker> markers)
        {
            bool all = true;
            for (int m = 0; m + 1 < markers.Count && parts.Count < MaxParts * 2; m += 2)
            {
                var a = markers[m].Position.xz; var b = markers[m + 1].Position.xz;
                bool done = false;
                for (int k = 0; k < parts.Count && !done; k++)
                {
                    var part = LayerRegion.From(parts[k]);
                    if (part == null) continue;
                    var pa = part.ClosestOnBoundary(a, out int sa, out _);
                    var pb = part.ClosestOnBoundary(b, out int sb, out _);
                    if (math.distance(pa, a) > 1.5f || math.distance(pb, b) > 1.5f) continue;
                    var chord = new Chord { A = pa, B = pb, SegA = sa, SegB = sb };
                    if (!PolygonSplitter.SplitByChord(part.Ring, chord, out var left, out var right)) continue;
                    parts[k] = left;
                    parts.Add(right);
                    cuts.Add(chord);
                    done = true;
                }
                all &= done;
            }
            return all;
        }

        private static bool CutByRatio(LayerContext ctx, LayerRegion region, List<float2[]> parts, List<Chord> cuts)
        {
            int count = math.clamp(ctx.I("parts"), 2, MaxParts);
            region.PrincipalAxes(out _, out var major, out _, out _);
            float angle = math.radians(ctx.F("angle"));
            math.sincos(angle, out float s, out float c);
            var normal = new float2(major.x * c - major.y * s, major.x * s + major.y * c);
            float total = region.Area;
            var remaining = region.Ring;
            parts.Clear();
            for (int k = 0; k < count - 1; k++)
            {
                float share = count == 2 ? math.clamp(ctx.F("ratio"), 1f, 99f) / 100f : 1f / count;
                if (!PolygonSplitter.SplitByArea(remaining, normal, total * share, out var low, out var high))
                {
                    parts.Add(remaining);
                    return false;
                }
                parts.Add(low);
                cuts.Add(SharedChord(low, high));
                remaining = high;
            }
            parts.Add(remaining);
            return true;
        }

        /// <summary>The cut between two adjacent parts: the edge of `low` whose endpoints both lie on `high`'s outline.</summary>
        private static Chord SharedChord(float2[] low, float2[] high)
        {
            var best = new Chord();
            float bestLen = 0f;
            for (int i = 0; i < low.Length; i++)
            {
                var a = low[i]; var b = low[(i + 1) % low.Length];
                if (Poly.DistanceToBoundary(high, a) > 0.05f || Poly.DistanceToBoundary(high, b) > 0.05f) continue;
                if (Poly.DistanceToBoundary(high, (a + b) * 0.5f) > 0.05f) continue;
                float len = math.distance(a, b);
                if (len > bestLen) { bestLen = len; best = new Chord { A = a, B = b }; }
            }
            return best;
        }
    }
}
