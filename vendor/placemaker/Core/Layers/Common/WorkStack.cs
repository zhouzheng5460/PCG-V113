// The game's Managed folder ships Stack<T> in both mscorlib and System, which makes
// System.Collections.Generic.Stack<T> ambiguous in the standalone build (CS0433). This tiny
// list-backed stack avoids the type entirely.

using System.Collections.Generic;

namespace Placemaker.Core.Layers.Common
{
    public sealed class WorkStack<T>
    {
        private readonly List<T> _items = new List<T>();
        public int Count => _items.Count;
        public void Push(T item) => _items.Add(item);
        public T Pop()
        {
            int last = _items.Count - 1;
            var item = _items[last];
            _items.RemoveAt(last);
            return item;
        }
    }
}
