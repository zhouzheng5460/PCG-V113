// Placemaker — one undo/redo stack for everything the player edits in the tool: outline,
// markers and the design (recipe tree + values). Adapted from Grid Road Generator's History
// (snapshot of the stable state, gesture = one entry), but typed instead of reflection and
// with explicit commits + labels so the panel can say "Undo: move corner".
//
// Usage: the tool mutates its live state freely during a gesture (drag, slider preview) and
// calls Commit(newState, label) once when the gesture ends. History.Current is always the last
// committed state, so a cancelled gesture restores it without touching the stacks.

using System;
using System.Collections.Generic;

namespace Placemaker.Core.Tool
{
    public sealed class History<T> where T : class
    {
        private sealed class Entry
        {
            public T State;            // undo: state BEFORE the change; redo: state AFTER it
            public LocText Label;
            public string CoalesceKey;
            public double Time;
        }

        private readonly List<Entry> _undo = new List<Entry>();
        private readonly List<Entry> _redo = new List<Entry>();
        private readonly int _maxDepth;
        private readonly Func<T, T, bool> _sameState;
        private bool _coalesceBroken = true;

        /// <summary>Repeated commits with the same coalesce key closer together than this merge into one step.</summary>
        public double CoalesceWindowSeconds = 1.5;

        public History(int maxDepth = 80, Func<T, T, bool> sameState = null)
        {
            _maxDepth = Math.Max(1, maxDepth);
            _sameState = sameState;
        }

        /// <summary>The last committed state.</summary>
        public T Current { get; private set; }

        /// <summary>Incremented on every change of Current (commit, undo, redo, reset).</summary>
        public int Revision { get; private set; }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public int UndoDepth => _undo.Count;
        public int RedoDepth => _redo.Count;
        public LocText UndoLabel => _undo.Count > 0 ? _undo[_undo.Count - 1].Label : null;
        public LocText RedoLabel => _redo.Count > 0 ? _redo[_redo.Count - 1].Label : null;

        /// <summary>Forget all history and start from <paramref name="state"/> (tool opened, build finished, group loaded).</summary>
        public void Reset(T state)
        {
            _undo.Clear();
            _redo.Clear();
            Current = state;
            _coalesceBroken = true;
            Revision++;
        }

        /// <summary>
        /// Record a change. Returns false when nothing changed (state equal to Current per the
        /// comparer) — then no step is created. <paramref name="coalesceKey"/>: consecutive commits
        /// with the same key within <see cref="CoalesceWindowSeconds"/> become one undo step
        /// (keyboard nudges of a slider, repeated setValue of the same control).
        /// </summary>
        public bool Commit(T newState, LocText label, string coalesceKey = null, double nowSeconds = 0)
        {
            if (newState == null) throw new ArgumentNullException(nameof(newState));
            if (Current == null) { Reset(newState); return true; }
            if (_sameState != null && _sameState(Current, newState)) return false;

            var top = _undo.Count > 0 ? _undo[_undo.Count - 1] : null;
            bool merge = !_coalesceBroken && coalesceKey != null && top != null && top.CoalesceKey == coalesceKey
                         && nowSeconds - top.Time <= CoalesceWindowSeconds && _redo.Count == 0;
            if (merge)
            {
                top.Time = nowSeconds;
                top.Label = label ?? top.Label;
            }
            else
            {
                _undo.Add(new Entry { State = Current, Label = label, CoalesceKey = coalesceKey, Time = nowSeconds });
                if (_undo.Count > _maxDepth) _undo.RemoveAt(0);
            }
            _redo.Clear();
            Current = newState;
            _coalesceBroken = false;
            Revision++;
            return true;
        }

        /// <summary>The next commit starts a new undo step even if its coalesce key matches.</summary>
        public void BreakCoalescing() => _coalesceBroken = true;

        /// <summary>Steps back; returns the state to restore, or null when there is nothing to undo.</summary>
        public T Undo()
        {
            if (_undo.Count == 0) return null;
            var e = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(new Entry { State = Current, Label = e.Label });
            Current = e.State;
            _coalesceBroken = true;
            Revision++;
            return Current;
        }

        /// <summary>Steps forward; returns the state to restore, or null when there is nothing to redo.</summary>
        public T Redo()
        {
            if (_redo.Count == 0) return null;
            var e = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(new Entry { State = Current, Label = e.Label, Time = double.NegativeInfinity });
            if (_undo.Count > _maxDepth) _undo.RemoveAt(0);
            Current = e.State;
            _coalesceBroken = true;
            Revision++;
            return Current;
        }

        /// <summary>
        /// Replace Current without creating a step (e.g. re-sync derived data such as attachments
        /// recomputed after a road changed). Keeps both stacks.
        /// </summary>
        public void ReplaceCurrent(T state)
        {
            Current = state ?? throw new ArgumentNullException(nameof(state));
            Revision++;
        }
    }
}
