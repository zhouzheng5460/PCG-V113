// Placemaker — tiny stack/queue on List<T>.
//
// Why: the game's Managed folder ships both mscorlib and System with Stack<T>/Queue<T>, so the
// net48 mod build reports CS0433 (type exists in both). These replacements avoid the ambiguity
// without touching the project's references.

using System.Collections.Generic;

namespace Placemaker.Core.Geometry
{
    internal sealed class ListStack<T>
    {
        private readonly List<T> _items;
        public ListStack(int capacity = 16) { _items = new List<T>(capacity); }
        public int Count => _items.Count;
        public void Push(T item) => _items.Add(item);
        public T Peek() => _items[_items.Count - 1];
        public T Pop()
        {
            int last = _items.Count - 1;
            T item = _items[last];
            _items.RemoveAt(last);
            return item;
        }
        public void Clear() => _items.Clear();
    }

    /// <summary>FIFO queue on a growing list with a head index (amortised O(1), compacts when half empty).</summary>
    internal sealed class ListQueue<T>
    {
        private readonly List<T> _items;
        private int _head;
        public ListQueue(int capacity = 16) { _items = new List<T>(capacity); }
        public int Count => _items.Count - _head;
        public void Enqueue(T item) => _items.Add(item);
        public T Dequeue()
        {
            T item = _items[_head];
            _items[_head] = default;
            _head++;
            if (_head > 64 && _head * 2 > _items.Count)
            {
                _items.RemoveRange(0, _head);
                _head = 0;
            }
            return item;
        }
        public void Clear() { _items.Clear(); _head = 0; }
    }
}
