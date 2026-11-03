// Placemaker — s59: what the build does with the game's validation verdicts, in ONE pass (GPL-3.0).
//
// Root cause of "nine times out of ten it says parts overlap" (docs/proposals/s59.md): every piece we emit is a separate,
// top-level, Essential temporary (GenerateObjectsSystem gives every CreationDefinition TempFlags.Essential), so the game's
// ValidationHelpers check each of our pieces against every other one — stall marking against stall marking, bush against
// bush, marking against our own lot lanes — exactly as if a player had plopped 300 props on top of each other by hand, and
// raise ErrorSeverity.Error for every touching pair. The game's Apply never reads Error (only Warning → Deleted,
// Override → Overridden, TempFlags.Cancel → dropped); it was OUR gate that turned those errors into "leave pieces out
// and rebuild" six times and then fail.
//
// This class is the replacement for that loop: pure decision logic over the pieces the game flagged.
//   * Anarchy ("Ignore collisions in my builds", default on): nothing is ever refused or failed for a collision or
//     validation error — every planned net, object, decal, surface and area is applied, like the Anarchy mod does. The one
//     hard rule left: an existing building is never demolished. The game marks a building it would remove with a Warning
//     (the build strips it before Apply, so the building stands); an OBJECT/decal/surface/area of ours that touches an
//     existing building is left out (just that piece); a NET touching one is kept (removing an edge would break the
//     network) and reported with its position.
//   * Anarchy off (vanilla rules): flagged decorative pieces (objects, decals, surfaces, areas) are left out in place,
//     once — no re-emission, no retry. The build fails only when a NET is flagged or a piece touches an existing building,
//     and then names that piece and where it is.
//   * Not enough money is never a collision: it always stops the build (both modes).

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public enum VerdictKind { Net, Object, Decal, Surface, Area, Other }

    /// <summary>One planned piece the game flagged (Error or Warning on its temporary), as the build service saw it.</summary>
    public struct FlaggedPiece
    {
        /// <summary>The build's key of the plan item (meaningful only when <see cref="Ours"/>).</summary>
        public long Key;
        /// <summary>The flagged temporary is one of our planned pieces (false: a copy of an existing road, an unmatched sub-element).</summary>
        public bool Ours;
        public VerdictKind Kind;
        /// <summary>Stall marking, charger or arrow (PlanWork.IsFunctional): never "decorative".</summary>
        public bool Functional;
        /// <summary>The piece touches an existing building (lot or footprint, SiteSafety geometry).</summary>
        public bool TouchesBuilding;
        /// <summary>The game's error type ("OverlapExisting", "InvalidShape", "NotEnoughMoney"...).</summary>
        public string ErrorType;
        public string Name;
        public float3 At;

        public bool Decorative => !Functional && (Kind == VerdictKind.Object || Kind == VerdictKind.Decal || Kind == VerdictKind.Surface || Kind == VerdictKind.Area);
        public bool Droppable => Kind == VerdictKind.Object || Kind == VerdictKind.Decal || Kind == VerdictKind.Surface || Kind == VerdictKind.Area;
    }

    public sealed class VerdictDecision
    {
        /// <summary>Pieces to leave out in place (cancelled temporaries) with the audit reason.</summary>
        public readonly List<KeyValuePair<FlaggedPiece, string>> Drop = new List<KeyValuePair<FlaggedPiece, string>>();
        /// <summary>Pieces built although the game flagged them (anarchy).</summary>
        public readonly List<FlaggedPiece> KeptDespite = new List<FlaggedPiece>();
        /// <summary>Nets of ours that touch an existing building and are built anyway (the building is kept standing).</summary>
        public readonly List<FlaggedPiece> NetsByBuildings = new List<FlaggedPiece>();
        /// <summary>Set when the build must stop: the piece responsible (its name, kind and position) and the error type.</summary>
        public FlaggedPiece? Blocker;
        public string FailErrorType;
        public bool Fail => Blocker.HasValue;
    }

    public static class BuildVerdicts
    {
        public const string Money = "NotEnoughMoney";

        /// <summary>Error types that are about the whole build, not about a piece's place (excluding pieces cannot fix them).</summary>
        public static bool IsGlobal(string errorType) => errorType == Money;

        /// <summary>
        /// One pass over everything the game flagged. Deterministic (input order), never throws. Duplicate keys (several
        /// flagged temporaries of one plan item) are decided once.
        /// </summary>
        public static VerdictDecision Decide(IEnumerable<FlaggedPiece> flagged, bool anarchy, bool dropDecorative = true)
        {
            var d = new VerdictDecision();
            if (flagged == null) return d;
            var seen = new HashSet<long>();
            foreach (var p in flagged)
            {
                if (p.Ours && !seen.Add(p.Key)) continue;
                if (IsGlobal(p.ErrorType))
                {
                    if (!d.Fail) { d.Blocker = p; d.FailErrorType = p.ErrorType; }
                    continue;
                }
                if (p.TouchesBuilding)
                {
                    // Never demolish: our piece goes, the building stays. A net cannot go alone (it would break the network):
                    // anarchy keeps it (the building's Warning is stripped, nothing is removed); vanilla rules stop the build.
                    if (p.Droppable && p.Ours) { d.Drop.Add(new KeyValuePair<FlaggedPiece, string>(p, AuditReason.SiteSafety)); continue; }
                    if (anarchy) { d.NetsByBuildings.Add(p); d.KeptDespite.Add(p); continue; }
                    if (!d.Fail) { d.Blocker = p; d.FailErrorType = string.IsNullOrEmpty(p.ErrorType) ? "OverlapExisting" : p.ErrorType; }
                    continue;
                }
                if (anarchy)
                {
                    if (p.Ours) d.KeptDespite.Add(p);
                    continue;
                }
                if (!p.Ours) continue;   // vanilla rules: a flagged temporary that is not ours (an existing road copy) is the game's business
                if (p.Decorative && dropDecorative)
                {
                    d.Drop.Add(new KeyValuePair<FlaggedPiece, string>(p, AuditReason.Rejected(p.ErrorType)));
                    continue;
                }
                if (p.Functional && p.Droppable && dropDecorative)
                {
                    // A stall marking the game refuses under vanilla rules: leave that one out, never the whole lot.
                    d.Drop.Add(new KeyValuePair<FlaggedPiece, string>(p, AuditReason.Rejected(p.ErrorType)));
                    continue;
                }
                if (!d.Fail) { d.Blocker = p; d.FailErrorType = string.IsNullOrEmpty(p.ErrorType) ? "OverlapExisting" : p.ErrorType; }
            }
            return d;
        }

        /// <summary>"net 'Alley' at (612, -491)" — the piece named in a failure message / the log.</summary>
        public static string Describe(FlaggedPiece p)
        {
            string kind = p.Kind.ToString().ToLowerInvariant();
            string name = string.IsNullOrEmpty(p.Name) ? "?" : p.Name;
            return $"{kind} '{name}' at ({p.At.x:F0}, {p.At.z:F0})";
        }

        /// <summary>Counts per error type and kind for the one log line ("OverlapExisting×130 [decal 'ParkingLotDecal01' ×61; ...]").</summary>
        public static string Histogram(IEnumerable<FlaggedPiece> flagged, int top = 3)
        {
            if (flagged == null) return "";
            var byType = new SortedDictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in flagged)
            {
                string t = string.IsNullOrEmpty(p.ErrorType) ? "Unknown" : p.ErrorType;
                counts[t] = (counts.TryGetValue(t, out int n) ? n : 0) + 1;
                if (!byType.TryGetValue(t, out var what)) byType[t] = what = new Dictionary<string, int>(StringComparer.Ordinal);
                string w = p.Kind.ToString().ToLowerInvariant() + " '" + (string.IsNullOrEmpty(p.Name) ? "?" : p.Name) + "'";
                what[w] = (what.TryGetValue(w, out int m) ? m : 0) + 1;
            }
            var parts = new List<string>();
            foreach (var kv in byType)
            {
                var list = new List<KeyValuePair<string, int>>(kv.Value);
                list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
                var tops = new List<string>();
                for (int i = 0; i < list.Count && i < top; i++) tops.Add(list[i].Key + " ×" + list[i].Value);
                parts.Add(kv.Key + "×" + counts[kv.Key] + " [" + string.Join("; ", tops) + "]");
            }
            return string.Join(", ", parts);
        }
    }
}
