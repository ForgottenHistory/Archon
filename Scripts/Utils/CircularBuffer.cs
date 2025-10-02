using System;
using System.Collections.Generic;

namespace Utils
{
    /// <summary>
    /// Fixed-size circular buffer for bounded data storage
    /// Prevents unbounded growth in late-game scenarios
    /// Used for province history, event logs, etc.
    /// </summary>
    public class CircularBuffer<T>
    {
        private readonly T[] buffer;
        private readonly int capacity;
        private int head;
        private int tail;
        private int count;

        public CircularBuffer(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be positive", nameof(capacity));

            this.capacity = capacity;
            this.buffer = new T[capacity];
            this.head = 0;
            this.tail = 0;
            this.count = 0;
        }

        public int Count => count;
        public int Capacity => capacity;
        public bool IsEmpty => count == 0;
        public bool IsFull => count == capacity;

        /// <summary>
        /// Add item to buffer (overwrites oldest if full)
        /// </summary>
        public void Add(T item)
        {
            buffer[tail] = item;
            tail = (tail + 1) % capacity;

            if (IsFull)
            {
                // Buffer is full, move head forward (overwrite oldest)
                head = (head + 1) % capacity;
            }
            else
            {
                count++;
            }
        }

        /// <summary>
        /// Remove and return oldest item
        /// </summary>
        public T RemoveOldest()
        {
            if (IsEmpty)
                throw new InvalidOperationException("Buffer is empty");

            T item = buffer[head];
            buffer[head] = default(T); // Clear reference
            head = (head + 1) % capacity;
            count--;

            return item;
        }

        /// <summary>
        /// Get most recent N items without removing them
        /// </summary>
        public IEnumerable<T> GetMostRecent(int count)
        {
            if (count <= 0)
                yield break;

            int itemsToReturn = Math.Min(count, this.count);
            int index = tail - 1;

            for (int i = 0; i < itemsToReturn; i++)
            {
                if (index < 0)
                    index = capacity - 1;

                yield return buffer[index];
                index--;
            }
        }

        /// <summary>
        /// Get oldest N items without removing them
        /// </summary>
        public IEnumerable<T> GetOldest(int count)
        {
            if (count <= 0)
                yield break;

            int itemsToReturn = Math.Min(count, this.count);
            int index = head;

            for (int i = 0; i < itemsToReturn; i++)
            {
                yield return buffer[index];
                index = (index + 1) % capacity;
            }
        }

        /// <summary>
        /// Get all items from oldest to newest
        /// </summary>
        public IEnumerable<T> GetAll()
        {
            return GetOldest(count);
        }

        /// <summary>
        /// Clear all items
        /// </summary>
        public void Clear()
        {
            Array.Clear(buffer, 0, capacity);
            head = 0;
            tail = 0;
            count = 0;
        }

        /// <summary>
        /// Peek at newest item without removing
        /// </summary>
        public T PeekNewest()
        {
            if (IsEmpty)
                throw new InvalidOperationException("Buffer is empty");

            int newestIndex = tail - 1;
            if (newestIndex < 0)
                newestIndex = capacity - 1;

            return buffer[newestIndex];
        }

        /// <summary>
        /// Peek at oldest item without removing
        /// </summary>
        public T PeekOldest()
        {
            if (IsEmpty)
                throw new InvalidOperationException("Buffer is empty");

            return buffer[head];
        }
    }
}