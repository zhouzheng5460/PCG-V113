// DESIGN RULE — "ground before props" (s8, player: "for the crop fields you can just use a texture and
// draw on the ground … there's a lot of instances where you shouldn't just be placing a billion things").
//
// Anything that reads as a TEXTURE from the game camera is ground, not props:
//   fields, crop beds, lawns, parterres, moss / gravel beds, bamboo groves' floor, storage pads, yards.
// Those are SURFACE polygons (or terrain PAINT when no fitting surface exists), one per field/bed.
// Props are only ACCENTS that tell the eye what the ground is: a few sample plants on the row heads
// along a headland, a scarecrow, a shed, trellis ends, a representative stack on a pad, edge hedges.
//   * One field = one surface + at most a few dozen accents, never a plant every metre.
//   * Row direction: CS2 surfaces are world-mapped (their texture cannot be rotated), so rows are
//     shown by row-head accents and, when the farming decal set is installed, a line of wheel-rut
//     decals ("decal.farm") laid along the planned row direction.
//   * Every layer keeps its own object budget; LayerBase additionally caps every plan (ObjectBudget).
//
// Categories (automatic pools, curated by GroundCoverCategoryRules below):
//   surface.crop            any farmland texture (vanilla "Agriculture Surface 01" first)
//   surface.crop.soil       bare / tilled soil          (MudSoil…, Soil)
//   surface.crop.plowed     ploughed / raked soil       (MudPlowed…, Raked)
//   surface.crop.rows       vegetable rows on soil      (CropRows…)
//   surface.crop.green      green crop / grass rows     (GrassCrop Green, GrassRows, GrassLines)
//   surface.crop.straw      cereal / stubble            (StrawFlat…, StrawRows…)
//   surface.crop.flower     flowering crop (any colour) (GrassCrop Blue/Red/Yellow)
//   surface.crop.lavender   purple/blue flowering crop  (GrassCrop Blue/Red)
//   surface.crop.yellow     rapeseed / sunflower yellow (GrassCrop Yellow)
//   decal.farm              straight wheel-rut / field-track decals (Field/Tractor/Grass Tracks)
// Fallbacks (CategoryFallbacks): every surface.crop.* → its parent → surface.crop → surface.dirt →
// surface.natural, so a playset without the farming pack still gets the vanilla Agriculture Surface.

using System.Collections.Generic;
using Placemaker.Core.Build;
using Placemaker.Core.Geometry;
using Unity.Mathematics;

namespace Placemaker.Core
{
    public static partial class AssetCategories
    {
        public const string SurfaceCrop = "surface.crop";
        public const string SurfaceCropSoil = "surface.crop.soil";
        public const string SurfaceCropPlowed = "surface.crop.plowed";
        public const string SurfaceCropRows = "surface.crop.rows";
        public const string SurfaceCropGreen = "surface.crop.green";
        public const string SurfaceCropStraw = "surface.crop.straw";
        public const string SurfaceCropFlower = "surface.crop.flower";
        public const string SurfaceCropLavender = "surface.crop.lavender";
        public const string SurfaceCropYellow = "surface.crop.yellow";
        public const string DecalFarm = "decal.farm";
    }
}

namespace Placemaker.Core.Build
{
    /// <summary>Name curation for the farm ground categories (surfaces + track decals). See GroundCover.</summary>
    public sealed class GroundCoverCategoryRules : ICategoryRules, ISurfaceCategoryRules
    {
        public int Priority => 25;

        private static bool Has(string s, params string[] parts) => AssetHeuristics.ContainsAny(s, parts);

        /// <summary>Clones / overlays that must never be an automatic field texture.</summary>
        public static bool NotAField(string lower) =>
            lower.StartsWith("placemaker overlay", System.StringComparison.Ordinal) || lower.StartsWith("plt ", System.StringComparison.Ordinal)
            || Has(lower, "overlay", "hedgebase", "hedge base", "placeholder");

        public void ClassifySurface(string lower, List<string> r)
        {
            if (string.IsNullOrEmpty(lower) || NotAField(lower)) return;
            bool farmPack = Has(lower, "farming surface", "haristess");
            bool farmland = farmPack || Has(lower, "agricultur", "farmland", "cropland", "crop field", "field surface");
            bool soil = Has(lower, "mudsoil", "mud soil", "tilled", "soil ", "soil s", "soil m") || lower.EndsWith("soil", System.StringComparison.Ordinal);
            if (soil && !Has(lower, "under", "legacy") || farmland) Add(r, AssetCategories.SurfaceCrop);
            if (!farmland && !soil) return;
            if (Has(lower, "plowed", "ploughed", "plough", "furrow", "raked")) Add(r, AssetCategories.SurfaceCropPlowed);
            else if (Has(lower, "mudsoil", "mudfiller", "mud soil") || soil) Add(r, AssetCategories.SurfaceCropSoil);
            if (Has(lower, "croprows", "crop rows", "vegetable")) Add(r, AssetCategories.SurfaceCropRows);
            if (Has(lower, "strawflat", "strawrows", "straw", "stubble", "wheat", "cereal")) Add(r, AssetCategories.SurfaceCropStraw);
            bool colour = Has(lower, " blue", " red", " yellow", "purple", "lavender", "rapeseed", "sunflower");
            if (Has(lower, "grasscrop", "grassrows", "grasslines", "green crop", "greencrop") && !colour) Add(r, AssetCategories.SurfaceCropGreen);
            if (colour && (Has(lower, "grasscrop", "crop", "flower", "lavender", "rapeseed", "sunflower")))
            {
                Add(r, AssetCategories.SurfaceCropFlower);
                if (Has(lower, " blue", " red", "purple", "lavender")) Add(r, AssetCategories.SurfaceCropLavender);
                if (Has(lower, " yellow", "rapeseed", "sunflower")) Add(r, AssetCategories.SurfaceCropYellow);
            }
        }

        /// <summary>Straight track decals of farming decal sets (wheel ruts along crop rows).</summary>
        public static void ClassifyDecal(string lower, List<string> r)
        {
            if (Has(lower, "fieldtracks", "field tracks", "tractortracks", "tractor tracks", "grasstracks", "grass tracks", "wheel ruts", "wheelruts"))
                Add(r, AssetCategories.DecalFarm);
        }

        /// <summary>Automatic farm-track decals: straight full-length pieces only (corners and ends need a layout).</summary>
        public static bool DecalAuto(string lower) =>
            Has(lower, "straight") && !Has(lower, "half", " end", "corner", "bend", "placeholder");

        public bool Classify(string lower, AssetTraits traits, List<string> categories) => false;

        public bool? AutoEligible(string category, string lower, AssetTraits traits) =>
            category == AssetCategories.DecalFarm ? DecalAuto(lower) : (bool?)null;

        /// <summary>Pool preference inside the crop surface categories: the plain base texture first.</summary>
        public static int Score(string category, string lower)
        {
            if (category == null || !category.StartsWith(AssetCategories.SurfaceCrop, System.StringComparison.Ordinal)) return 0;
            int s = 0;
            if (Has(lower, "agriculture surface 01")) s += 200;
            if (Has(lower, "lighter", "faded", "darker", "brown", "legacy", "under", "filler")) s -= 30;
            if (Has(lower, "1 surface", "1 green surface", "rows surface", "1 surface")) s += 10;
            if (category == AssetCategories.SurfaceCropGreen && Has(lower, "grasscrop1 green surface")) s += 40;
            if (category == AssetCategories.SurfaceCropLavender && Has(lower, " blue")) s += 40;
            if (category == AssetCategories.SurfaceCropStraw && Has(lower, "golden")) s += 25;
            if (category == AssetCategories.SurfaceCropPlowed && Has(lower, "plowed1")) s += 25;
            if (category == AssetCategories.SurfaceCropSoil && Has(lower, "mudsoil1 surface")) s += 25;
            return s;
        }

        private static void Add(List<string> r, string c) { if (!r.Contains(c)) r.Add(c); }
    }
}

namespace Placemaker.Core.Layers.Common
{
    public static class GroundCover
    {
        // Crop kinds a layer asks for (what the field looks like from the camera).
        public const string Soil = "soil", Plowed = "plowed", Rows = "rows", Green = "green", Straw = "straw",
            Flower = "flower", Lavender = "lavender", Yellow = "yellow";

        public const string PaintedKey = "Placemaker.GroundCover.Issue.Painted";

        public static string CategoryFor(string kind)
        {
            switch (kind)
            {
                case Soil: return AssetCategories.SurfaceCropSoil;
                case Plowed: return AssetCategories.SurfaceCropPlowed;
                case Rows: return AssetCategories.SurfaceCropRows;
                case Green: return AssetCategories.SurfaceCropGreen;
                case Straw: return AssetCategories.SurfaceCropStraw;
                case Flower: return AssetCategories.SurfaceCropFlower;
                case Lavender: return AssetCategories.SurfaceCropLavender;
                case Yellow: return AssetCategories.SurfaceCropYellow;
                default: return AssetCategories.SurfaceCrop;
            }
        }

        /// <summary>First category of the fallback chain with installed items (null = nothing at all).</summary>
        public static string Resolve(string category)
        {
            foreach (var c in CategoryFallbacks.Chain(category)) if (AssetAvailability.Has(c)) return c;
            return null;
        }

        /// <summary>
        /// One field of <paramref name="kind"/>: a single surface polygon (the player's pick, else the crop
        /// category with fallbacks). Without any farmland/dirt surface installed the field is painted on the
        /// terrain instead (layer <paramref name="paintLayer"/>, 0 = no paint). Returns false when neither happened.
        /// </summary>
        public static bool Field(LayerContext ctx, float2[] ring, string kind, string chosenId = "", int paintLayer = 0,
            SurfaceRole role = SurfaceRole.Decorative)
        {
            if (ring == null || ring.Length < 3) return false;
            string cat = CategoryFor(kind);
            if (!string.IsNullOrEmpty(chosenId)) return ctx.Surface(ring, role, AssetMix.One(chosenId), cat);
            var resolved = Resolve(cat);
            if (resolved != null) return ctx.Surface(ring, role, AssetMix.One(""), cat);
            if (paintLayer <= 0) return false;
            bool painted = TerrainPaint.Patch(ctx, ring, paintLayer, 0.9f, 1.5f);
            if (painted && !HasIssue(ctx, PaintedKey)) ctx.Issue(IssueSeverity.Info, PaintedKey);
            return painted;
        }

        /// <summary>
        /// Wheel ruts along the rows: one line of straight track decals (≈12 m pieces) through the field along
        /// <paramref name="dir"/>, only when a farm-track decal set is installed. Capped per field and per plan.
        /// </summary>
        public static int RowTracks(LayerContext ctx, Occupancy occ, float2[] field, float2 dir, ref int budget, uint salt, int maxPerField = 6, float pieceLength = 12f)
        {
            if (budget <= 0 || !AssetAvailability.Has(AssetCategories.DecalFarm) || field == null) return 0;
            if (Poly.Area(field) < 250f) return 0;
            dir = math.normalizesafe(dir, new float2(0, 1));
            var c = Poly.Centroid(field);
            if (!Poly.Contains(field, c)) return 0;
            // Chord through the centroid along the rows.
            float lo = 0f, hi = 0f;
            const float stepScan = 1f;
            while (lo > -500f && Poly.Contains(field, c + dir * (lo - stepScan))) lo -= stepScan;
            while (hi < 500f && Poly.Contains(field, c + dir * (hi + stepScan))) hi += stepScan;
            float len = hi - lo - 2f;
            int n = math.min(maxPerField, (int)math.floor(len / pieceLength));
            if (n <= 0) return 0;
            float start = lo + 1f + (len - n * pieceLength) * 0.5f + pieceLength * 0.5f;
            float yaw = math.atan2(dir.x, dir.y);
            int placed = 0;
            for (int i = 0; i < n && budget > 0; i++)
            {
                var p = c + dir * (start + i * pieceLength);
                if (occ != null && (occ.Corridors.Overlaps(p, 1f, 0f) || occ.HitsPolygon(p, 1f))) continue;   // never over a path or a reserved corridor
                ctx.Object(p, yaw, ObjectRole.Decal, AssetMix.One(""), Hashing.Mix(salt, 0xD7u), -1, AssetCategories.DecalFarm);
                placed++; budget--;
            }
            return placed;
        }

        /// <summary>
        /// Sample plants on the row heads: the first <paramref name="depth"/> metres of every
        /// <paramref name="everyNth"/>-th row at both ends of the field (where the headland / track is),
        /// so a surface field reads as planted rows. Capped by <paramref name="maxPerField"/> and the plan budget.
        /// </summary>
        public static int RowHeads(LayerContext ctx, Occupancy occ, float2[] field, float2 dir, float rowGap, float step, float depth, int everyNth,
            ObjectRole role, string category, string[] ids, int maxPerField, ref int budget, uint salt, bool bothEnds = true)
        {
            if (category == null || budget <= 0 || maxPerField <= 0) return 0;
            int n = 0, row = 0;
            everyNth = math.max(1, everyNth);
            var lines = RowLines(field, dir, rowGap, 0.6f);
            // Spread the accents over the whole headland: widen the row stride when the cap would run out
            // halfway (otherwise one corner of the field gets every plant).
            int perHead = math.max(1, (int)math.floor(depth / math.max(0.1f, step)) + 1);
            int wanted = (lines.Count + everyNth - 1) / everyNth * perHead * (bothEnds ? 2 : 1);
            if (wanted > maxPerField) everyNth = math.max(everyNth, (int)math.ceil(lines.Count * perHead * (bothEnds ? 2f : 1f) / maxPerField));
            foreach (var (a, b) in lines)
            {
                if (row++ % everyNth != 0) continue;
                float len = math.distance(a, b);
                if (len < step) continue;
                var t = (b - a) / len;
                float yaw = math.atan2(t.x, t.y);
                var asset = AssetMix.Pick(ids, Hashing.Mix(salt, (uint)row));
                for (int end = 0; end < (bothEnds ? 2 : 1); end++)
                {
                    var from = end == 0 ? a : b;
                    var along = end == 0 ? t : -t;
                    float reach = math.min(depth, len * (bothEnds ? 0.5f : 1f));
                    for (float s = 0f; s <= reach; s += step)
                    {
                        if (n >= maxPerField || budget <= 0) return n;
                        var p = from + along * s;
                        if (occ != null && !occ.Free(p, 0.45f, 0f, 0.1f)) continue;
                        ctx.Object(p, yaw, role, asset, Hashing.Mix(salt, (uint)row), -1, category);
                        if (occ != null) occ.Take(p, 0.3f, 1);
                        n++; budget--;
                    }
                }
            }
            return n;
        }

        /// <summary>Parallel row lines across a field (clipped to it, ends pulled in by <paramref name="endInset"/>).</summary>
        public static List<(float2 a, float2 b)> RowLines(float2[] field, float2 dir, float spacing, float endInset)
        {
            var result = new List<(float2, float2)>();
            if (field == null || field.Length < 3 || spacing <= 0.1f) return result;
            dir = math.normalizesafe(dir, new float2(0, 1));
            var n = new float2(-dir.y, dir.x);
            float minN = float.MaxValue, maxN = float.MinValue;
            foreach (var p in field) { float d = math.dot(p, n); minN = math.min(minN, d); maxN = math.max(maxN, d); }
            int count = 0;
            for (float o = minN + spacing * 0.5f; o < maxN && count < 2000; o += spacing, count++)
            {
                // Intersections of the line {x : dot(x,n) = o} with the polygon edges.
                var ts = new List<float>();
                for (int i = 0, j = field.Length - 1; i < field.Length; j = i++)
                {
                    float di = math.dot(field[i], n) - o, dj = math.dot(field[j], n) - o;
                    if ((di > 0f) == (dj > 0f)) continue;
                    float k = di / (di - dj);
                    var x = math.lerp(field[i], field[j], k);
                    ts.Add(math.dot(x, dir));
                }
                ts.Sort();
                var basePt = n * o;
                for (int k = 0; k + 1 < ts.Count; k += 2)
                {
                    float t0 = ts[k] + endInset, t1 = ts[k + 1] - endInset;
                    if (t1 - t0 < 1f) continue;
                    result.Add((basePt + dir * t0, basePt + dir * t1));
                }
            }
            return result;
        }

        /// <summary>
        /// A hedge as a ROW of hedge pieces laid end to end (one piece per piece length, evenly spread over
        /// the run), never a bush every metre. Piece length from the chosen asset's bounds, else
        /// <paramref name="fallbackPiece"/> (vanilla HedgePiece* are ≈ 4.1 m long; bush fallbacks ≈ 1.6 m).
        /// </summary>
        public static int HedgeRow(LayerContext ctx, float2 a, float2 b, AssetRef asset, string category, ObjectRole role, uint variation,
            float fallbackPiece = 4f, Occupancy occ = null)
        {
            float len = math.distance(a, b);
            if (len < 0.5f) return 0;
            float piece = fallbackPiece;
            if (!string.IsNullOrEmpty(asset.Id) && ctx.Site?.Assets != null && ctx.Site.Assets.TryGetValue(asset.Id, out var info))
            {
                float z = info.BoundsMax.z - info.BoundsMin.z;
                if (z > 0.5f && math.isfinite(z)) piece = z;
            }
            if (category == AssetCategories.Bush) piece = math.min(piece, 1.6f);
            if (len < piece * 0.75f) return 0;   // a piece would overhang the run by more than an eighth each end: leave it open
            int n = math.max(1, (int)math.round(len / piece));
            var d = (b - a) / len;
            float yaw = math.atan2(d.x, d.y), step = len / n;
            int placed = 0;
            for (int i = 0; i < n; i++)
            {
                var p = a + d * (step * (i + 0.5f));
                if (occ != null && occ.Corridors.Overlaps(p, 0.4f, 0f)) continue;
                ctx.Object(p, yaw, role, asset, variation, -1, category);
                placed++;
            }
            return placed;
        }

        /// <summary>Accent budget for a layer: <paramref name="perHa"/> objects per hectare, clamped.</summary>
        public static int AccentBudget(float areaM2, float perHa, int min, int max) =>
            math.clamp((int)math.round(areaM2 / 10000f * perHa), min, max);

        private static bool HasIssue(LayerContext ctx, string key)
        {
            foreach (var i in ctx.Plan.Issues) if (i.Key == key) return true;
            return false;
        }
    }
}
