// s53 — build audit: everything that did not make it into the final placement, with the reason.
//
// The build service records one entry per planned piece it left out (or kept despite a game error) and the plan-time drops
// (declutter). Pure data + formatting: the log block, the ModsData/Placemaker/audit JSON and the "Left out" chip in the panel
// (summary.audit) all read from here.

using System;
using System.Collections.Generic;
using System.Text;
using Placemaker.Core.Tool;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public static class AuditReason
    {
        public const string RejectedPrefix = "rejected:";          // "rejected:OverlapExisting"
        public const string Anarchy = "anarchy";                   // built although the game reported an overlap (kept, not left out)
        public const string SiteSafety = "site-safety";            // an existing building was in the way
        public const string Declutter = "declutter";               // dropped by the planner (overlaps another piece)
        public const string Budget = "budget";                     // object budget
        public const string AssetMissing = "asset-missing";        // no usable asset in this playset
        public const string NoFit = "no-fit";                      // the layout could not fit it / unusable shape
        public const string ZoningMismatch = "zoning";            // cells shown in the preview that the game does not have zoned
        public const string Hidden = "hidden";                     // built, then hidden by the game after Apply (Overridden: overlap)
        public const string Other = "other";

        public static string Rejected(string errorType) => RejectedPrefix + (string.IsNullOrEmpty(errorType) ? "Unknown" : errorType);
        public static bool IsKept(string reason) => reason == Anarchy;

        /// <summary>Localisation key of the reason ("Placemaker.Audit.Reason.rejected" with the error type as {0} for rejects).</summary>
        public static string LabelKey(string reason) => reason.StartsWith(RejectedPrefix, StringComparison.Ordinal)
            ? "Placemaker.Audit.Reason.rejected" : "Placemaker.Audit.Reason." + reason;

        public static string Arg(string reason) => reason.StartsWith(RejectedPrefix, StringComparison.Ordinal) ? reason.Substring(RejectedPrefix.Length) : "";
    }

    public sealed class AuditEntry
    {
        public string Kind = "";      // net | object | decal | surface | area | terraform | paint | zoning
        public string Prefab = "";
        public string Reason = "";
        public float3 At;
        public int Count = 1;         // >1 for plan-time aggregates (position = the first)
    }

    public sealed class BuildAudit
    {
        public static readonly string[] Kinds = { "net", "object", "decal", "surface", "area", "terraform", "paint", "zoning" };
        public const int MaxMarkers = 400;

        public int Planned, Built;
        public readonly List<AuditEntry> Entries = new List<AuditEntry>();

        public void Add(string kind, string prefab, float3 at, string reason, int count = 1)
            => Entries.Add(new AuditEntry { Kind = kind ?? "object", Prefab = prefab ?? "", At = at, Reason = reason ?? AuditReason.Other, Count = Math.Max(1, count) });

        /// <summary>Pieces left out of the build (anarchy-kept pieces are built, so not counted).</summary>
        public int LeftOut { get { int n = 0; foreach (var e in Entries) if (!AuditReason.IsKept(e.Reason)) n += e.Count; return n; } }

        public int Kept { get { int n = 0; foreach (var e in Entries) if (AuditReason.IsKept(e.Reason)) n += e.Count; return n; } }

        public sealed class ReasonRow
        {
            public string Reason = "";
            public int Count;
            public readonly Dictionary<string, int> ByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> ByPrefab = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<float3> Positions = new List<float3>();
        }

        /// <summary>Rows by reason, biggest first (ties by name).</summary>
        public List<ReasonRow> Rows()
        {
            var map = new Dictionary<string, ReasonRow>(StringComparer.Ordinal);
            foreach (var e in Entries)
            {
                if (!map.TryGetValue(e.Reason, out var r)) map[e.Reason] = r = new ReasonRow { Reason = e.Reason };
                r.Count += e.Count;
                r.ByKind[e.Kind] = (r.ByKind.TryGetValue(e.Kind, out int k) ? k : 0) + e.Count;
                string p = string.IsNullOrEmpty(e.Prefab) ? "?" : e.Prefab;
                r.ByPrefab[p] = (r.ByPrefab.TryGetValue(p, out int n) ? n : 0) + e.Count;
                if (r.Positions.Count < MaxMarkers && math.all(math.isfinite(e.At))) r.Positions.Add(e.At);
            }
            var rows = new List<ReasonRow>(map.Values);
            rows.Sort((a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : string.CompareOrdinal(a.Reason, b.Reason));
            return rows;
        }

        public List<float3> Positions(string reason)
        {
            foreach (var r in Rows()) if (r.Reason == reason) return r.Positions;
            return new List<float3>();
        }

        private static string Top(Dictionary<string, int> d, int n)
        {
            var l = new List<KeyValuePair<string, int>>(d);
            l.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            var sb = new StringBuilder();
            for (int i = 0; i < l.Count && i < n; i++) sb.Append(i > 0 ? "; " : "").Append(l[i].Key).Append(" x").Append(l[i].Value);
            return sb.ToString();
        }

        /// <summary>The log block: "build audit: planned N, built M, left out K" then the kinds and one line per reason.</summary>
        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append("build audit: planned ").Append(Planned).Append(", built ").Append(Built).Append(", left out ").Append(LeftOut);
            if (Kept > 0) sb.Append(" (+").Append(Kept).Append(" kept despite collisions)");
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in Entries) if (!AuditReason.IsKept(e.Reason)) kinds[e.Kind] = (kinds.TryGetValue(e.Kind, out int n) ? n : 0) + e.Count;
            if (kinds.Count > 0)
            {
                sb.Append("\n  by kind:");
                foreach (var k in Kinds) if (kinds.TryGetValue(k, out int n)) sb.Append(' ').Append(k).Append(' ').Append(n);
            }
            foreach (var r in Rows())
                sb.Append("\n  ").Append(r.Reason).Append(" x").Append(r.Count).Append(" [").Append(Top(r.ByKind, 8)).Append("] top: ").Append(Top(r.ByPrefab, 3));
            return sb.ToString();
        }

        /// <summary>The full per-item list for ModsData/Placemaker/audit.</summary>
        public string ToJson(int groupId, string time)
        {
            var o = JsonValue.Obj().Set("group", groupId).Set("time", time).Set("planned", Planned).Set("built", Built)
                .Set("leftOut", LeftOut).Set("kept", Kept);
            var items = JsonValue.Arr();
            foreach (var e in Entries)
                items.Add(JsonValue.Obj().Set("kind", e.Kind).Set("prefab", e.Prefab).Set("reason", e.Reason).Set("count", e.Count)
                    .Set("x", Math.Round(e.At.x, 2)).Set("y", Math.Round(e.At.y, 2)).Set("z", Math.Round(e.At.z, 2)));
            o.Set("items", items);
            return o.ToString();
        }

        /// <summary>summary.audit for the panel chip: total left out and the reasons with counts (null when nothing to say).</summary>
        public JsonValue ToUi()
        {
            if (Entries.Count == 0) return null;
            var o = JsonValue.Obj().Set("leftOut", LeftOut).Set("kept", Kept);
            var arr = JsonValue.Arr();
            foreach (var r in Rows())
                arr.Add(JsonValue.Obj().Set("id", r.Reason).Set("label", AuditReason.LabelKey(r.Reason)).Set("arg", AuditReason.Arg(r.Reason))
                    .Set("count", r.Count).Set("kept", AuditReason.IsKept(r.Reason)).Set("top", Top(r.ByPrefab, 2)));
            o.Set("reasons", arr);
            return o;
        }
    }
}
