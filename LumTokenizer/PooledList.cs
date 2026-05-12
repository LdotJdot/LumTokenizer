using System;
using System.Collections.Generic;
using System.Text;

namespace LumTokenizer
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// 池化 List&lt;T&gt;，用完 using 自动归还。
    /// <para>
    /// <b>线程安全性：本类型不是线程安全的。</b>仅适用于单线程或外部已做互斥的场景；
    /// 多线程并发 <c>Add</c> 会同时损坏 <c>_count</c> 与 <c>_buffer</c>。
    /// 并发场景请改用 <see cref="System.Collections.Concurrent"/> 下的容器。
    /// </para>
    /// </summary>
    public sealed class PooledList<T> : IDisposable
    {
        private readonly ArrayPool<T> _pool;
        private T[] _buffer;
        private int _count;

        public PooledList(int initialCapacity = 32, ArrayPool<T>? pool = null)
        {
            _pool = pool ?? ArrayPool<T>.Shared;
            _buffer = _pool.Rent(initialCapacity);
            _count = 0;
        }

        public int Count => _count;
        public int Capacity => _buffer.Length;

        public void Add(T item)
        {
            if (_count == _buffer.Length) Grow();
            _buffer[_count++] = item;
        }

        public void Clear() => _count = 0;          // 只清计数，数组不复用

        public Span<T> AsSpan() => _buffer.AsSpan(0, _count);
        public T[] ToArray() => _buffer.AsSpan(0, _count).ToArray();
        public Memory<T> AsMemory() => _buffer.AsMemory(0, _count);

        // 显式还池
        public void Dispose()
        {
            if (_buffer == null) return;
            _pool.Return(_buffer, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            _buffer = null!;
            _count = 0;
        }

        private void Grow()
        {
            var next = _pool.Rent(_buffer.Length * 2);
            Array.Copy(_buffer, next, _count);
            _pool.Return(_buffer, clearArray: false);
            _buffer = next;
        }

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count) ThrowIndexOutOfRange();
                return _buffer[index];
            }
            set
            {
                if ((uint)index >= (uint)_count) ThrowIndexOutOfRange();
                _buffer[index] = value;
            }
        }

        private static void ThrowIndexOutOfRange() =>
            throw new ArgumentOutOfRangeException("index");
    }
}
