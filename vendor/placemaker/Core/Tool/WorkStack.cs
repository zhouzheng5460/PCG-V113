using System.Collections.Generic;

namespace Placemaker.Core.Tool
{
    /// <summary>
    /// List-backed LIFO. System.Collections.Generic.Stack&lt;T&gt; is ambiguous when compiling against
    /// the game's Managed folder (it exists in both its mscorlib and System), so Core avoids it.
    /// </summary>
    internal sealed class WorkStack<T>
    {
        private readonly List<T> _items = new List<T>();
        public int Count => _items.Count;
        public void Push(T item) => _items.Add(item);
        public T Pop()
        {
            var last = _items[_items.Count - 1];
            _items.RemoveAt(_items.Count - 1);
            return last;
        }
    }
}
