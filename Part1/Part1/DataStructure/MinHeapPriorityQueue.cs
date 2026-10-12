using System;
using System.Collections.Generic;

namespace Part1.DataStructures
{
    /// <summary>
    /// Generic binary min-heap priority queue (.NET Framework 4.7.2 has no built-in one).
    /// Enqueue/Dequeue are O(log n). A sequence number breaks ties so packets with the
    /// same priority still come out in arrival order (stable / FIFO within a level).
    /// Ref: Cormen et al. (2009) Ch. 6 "Heapsort" - heap-based priority queues.
    /// </summary>
    public sealed class MinHeapPriorityQueue<T>
    {
        private struct Node
        {
            public T Item;
            public int Priority;
            public long Seq;
        }

        private readonly List<Node> _heap = new List<Node>();
        private long _seq;

        public int Count { get { return _heap.Count; } }

        public void Enqueue(T item, int priority)
        {
            _heap.Add(new Node { Item = item, Priority = priority, Seq = _seq++ });
            SiftUp(_heap.Count - 1);
        }

        public bool TryDequeue(out T item)
        {
            if (_heap.Count == 0) { item = default(T); return false; }

            item = _heap[0].Item;
            Node last = _heap[_heap.Count - 1];
            _heap.RemoveAt(_heap.Count - 1);
            if (_heap.Count > 0)
            {
                _heap[0] = last;
                SiftDown(0);
            }
            return true;
        }

        // true when a should leave the heap before b
        private static bool Before(Node a, Node b)
        {
            return a.Priority != b.Priority ? a.Priority < b.Priority : a.Seq < b.Seq;
        }

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (!Before(_heap[i], _heap[parent])) break;
                Swap(i, parent);
                i = parent;
            }
        }

        private void SiftDown(int i)
        {
            int n = _heap.Count;
            while (true)
            {
                int left = 2 * i + 1, right = left + 1, best = i;
                if (left < n && Before(_heap[left], _heap[best])) best = left;
                if (right < n && Before(_heap[right], _heap[best])) best = right;
                if (best == i) break;
                Swap(i, best);
                i = best;
            }
        }

        private void Swap(int a, int b)
        {
            Node tmp = _heap[a];
            _heap[a] = _heap[b];
            _heap[b] = tmp;
        }
    }
}