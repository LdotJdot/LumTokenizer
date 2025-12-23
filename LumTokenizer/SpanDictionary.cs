using System;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace LumTokenizer
{
    public class SpanDictionary<TValue> 
    {
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
        public IDisposable Read() => new Releaser(_lock, false);
        public IDisposable Write() => new Releaser(_lock, true);
        private readonly struct Releaser : IDisposable
        {
            private readonly ReaderWriterLockSlim _l;
            private readonly bool _isWrite;
            public Releaser(ReaderWriterLockSlim l, bool isWrite) 
            {
                _l = l;
                _isWrite = isWrite;
                if (isWrite) l.EnterWriteLock(); else l.EnterReadLock();
            }
            public void Dispose() { if (_isWrite) _l.ExitWriteLock(); else _l.ExitReadLock(); }
        }


        // constants for serialization
        private const string VersionName = "Version"; // Do not rename (binary serialization)
        private const string HashSizeName = "HashSize"; // Do not rename (binary serialization). Must save buckets.Length
        private const string KeyValuePairsName = "KeyValuePairs"; // Do not rename (binary serialization)
        private const string ComparerName = "Comparer"; // Do not rename (binary serialization)

        private int[]? _buckets;
        private Entry[]? _entries;
#if TARGET_64BIT
        private ulong _fastModMultiplier;
#endif
        private int _count;
        private int _freeList;
        private int _freeCount;
        private int _version;
        private KeyCollection? _keys;
        private ValueCollection? _values;
        private const int StartOfFreeList = -3;

        public SpanDictionary() : this(0) { }

        public void ThrowException(string msg = "")
        {
            throw new Exception(msg);
        }

        public SpanDictionary(int capacity)
        {
            if (capacity < 0)
            {
                throw new Exception();
            }

            if (capacity > 0)
            {
                Initialize(capacity);
            }
        }


        public int Count => _count - _freeCount;

        /// <summary>
        /// Gets the total numbers of elements the internal data structure can hold without resizing.
        /// </summary>
        public int Capacity => _entries?.Length ?? 0;

        public KeyCollection Keys => _keys ??= new KeyCollection(this);

        public ValueCollection Values => _values ??= new ValueCollection(this);
               
        public TValue this[ReadOnlySpan<char> key]
        {
            get
            {
                using var _ = Read();
                ref TValue value = ref FindValue(key);
                if (!Unsafe.IsNullRef(ref value))
                {
                    return value;
                }

                throw new KeyNotFoundException();
                return default;
            }
            set
            {
                using var _ = Write();
                bool modified = TryInsert(key, value, true);
                Debug.Assert(modified);
            }
        }

        public void Add(ReadOnlySpan<char> key, TValue value)
        {
            using var _ = Write();

            bool modified = TryInsert(key, value, true);
            Debug.Assert(modified); // If there was an existing key and the Add failed, an exception will already have been thrown.
        }

        public void Clear()
        {
            using var _ = Write();

            int count = _count;
            if (count > 0)
            {
                Debug.Assert(_buckets != null, "_buckets should be non-null");
                Debug.Assert(_entries != null, "_entries should be non-null");

                Array.Clear(_buckets);

                _count = 0;
                _freeList = -1;
                _freeCount = 0;
                Array.Clear(_entries, 0, count);
            }
        }

        public bool ContainsKey(ReadOnlySpan<char> key) =>
            !Unsafe.IsNullRef(ref FindValue(key));

        public bool ContainsValue(TValue value)
        {
            using var _ = Read();

            Entry[]? entries = _entries;
            if (value == null)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (entries![i].next >= -1 && entries[i].value == null)
                    {
                        return true;
                    }
                }
            }
            else if (typeof(TValue).IsValueType)
            {
                // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                for (int i = 0; i < _count; i++)
                {
                    if (entries![i].next >= -1 && EqualityComparer<TValue>.Default.Equals(entries[i].value, value))
                    {
                        return true;
                    }
                }
            }
            else
            {
                // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                // https://github.com/dotnet/runtime/issues/10050
                // So cache in a local rather than get EqualityComparer per loop iteration
                EqualityComparer<TValue> defaultComparer = EqualityComparer<TValue>.Default;
                for (int i = 0; i < _count; i++)
                {
                    if (entries![i].next >= -1 && defaultComparer.Equals(entries[i].value, value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }


        public Enumerator GetEnumerator() => new Enumerator(this, Enumerator.KeyValuePair);


        internal ref TValue FindValue(ReadOnlySpan<char> key)
        {
 
            ref Entry entry = ref Unsafe.NullRef<Entry>();
            if (_buckets != null)
            {
                Debug.Assert(_entries != null, "expected entries to be != null");

          
                    uint hashCode = (uint)key.GetSpanHashCode();
                    int i = GetBucket(hashCode);
                    Entry[]? entries = _entries;
                    uint collisionCount = 0;

                    // ValueType: Devirtualize with EqualityComparer<TKey>.Default intrinsic
                    i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
                    do
                    {
                        // Test in if to drop range check for following array access
                        if ((uint)i >= (uint)entries.Length)
                        {
                            goto ReturnNotFound;
                        }

                        entry = ref entries[i];
                        if (entry.hashCode == hashCode && key.Equals(entry.key,StringComparison.Ordinal))
                        {
                            goto ReturnFound;
                        }

                        i = entry.next;

                        collisionCount++;
                    } while (collisionCount <= (uint)entries.Length);

                    // The chain of entries forms a loop; which means a concurrent update has happened.
                    // Break out of the loop and throw, rather than looping forever.
                    goto ConcurrentOperation;
               
            }

            goto ReturnNotFound;

ConcurrentOperation:
            throw new InvalidOperationException("Concurrent operations not supported");
ReturnFound:
            ref TValue value = ref entry.value;
Return:
            return ref value;
ReturnNotFound:
            value = ref Unsafe.NullRef<TValue>();
            goto Return;
        }

        private int Initialize(int capacity)
        {
            int size = HashHelpers.GetPrime(capacity);
            int[] buckets = new int[size];
            Entry[] entries = new Entry[size];

            // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
            _freeList = -1;
#if TARGET_64BIT
            _fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)size);
#endif
            _buckets = buckets;
            _entries = entries;

            return size;
        }

        private bool TryInsert(ReadOnlySpan<char> key, TValue value, bool InsertionBehavior)
        {
            // NOTE: this method is mirrored in CollectionsMarshal.GetValueRefOrAddDefault below.
            // If you make any changes here, make sure to keep that version in sync as well.

            if (key.Length==0)
            {
                ArgumentNullException.ThrowIfNull("key");
                string a = "aa"; ;
                a.GetHashCode();
            }

            if (_buckets == null)
            {
                Initialize(0);
            }
            Debug.Assert(_buckets != null);

            Entry[]? entries = _entries;
            Debug.Assert(entries != null, "expected entries to be non-null");

            uint hashCode = (uint) key.GetSpanHashCode();// get spanHashCode(key);

            uint collisionCount = 0;
            ref int bucket = ref GetBucket(hashCode);
            int i = bucket - 1; // Value in _buckets is 1-based
            
            
                // ValueType: Devirtualize with EqualityComparer<TKey>.Default intrinsic
                while ((uint)i < (uint)entries.Length)
                {
                if (entries[i].hashCode == hashCode && key.Equals(entries[i].key, StringComparison.Ordinal))
                {
                    if (InsertionBehavior)
                    {
                        entries[i].value = value;
                        return true;
                    }
                    else
                    {
                        //ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                        throw new Exception("Adding duplicate key");
                    }

                    return false;
                    }

                    i = entries[i].next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        //ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                        throw new Exception("Concurrent operations not supported");
                }
                }
            

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                Debug.Assert((StartOfFreeList - entries[_freeList].next) >= -1, "shouldn't overflow because `next` cannot underflow");
                _freeList = StartOfFreeList - entries[_freeList].next;
                _freeCount--;
            }
            else
            {
                int count = _count;
                if (count == entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }
                index = count;
                _count = count + 1;
                entries = _entries;
            }

            ref Entry entry = ref entries![index];
            entry.hashCode = hashCode;
            entry.next = bucket - 1; // Value in _buckets is 1-based
            entry.key = key.ToString();
            entry.value = value;
            bucket = index + 1; // Value in _buckets is 1-based
            _version++;
                        
            return true;
        }

     
     
        private void Resize() => Resize(HashHelpers.ExpandPrime(_count), false);

        private void Resize(int newSize, bool forceNewHashCodes)
        {
            // Value types never rehash
            Debug.Assert(_entries != null, "_entries should be non-null");
            Debug.Assert(newSize >= _entries.Length);

            Entry[] entries = new Entry[newSize];

            int count = _count;
            Array.Copy(_entries, entries, count);

            

            // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
            _buckets = new int[newSize];
#if TARGET_64BIT
            _fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)newSize);
#endif
            for (int i = 0; i < count; i++)
            {
                if (entries[i].next >= -1)
                {
                    ref int bucket = ref GetBucket(entries[i].hashCode);
                    entries[i].next = bucket - 1; // Value in _buckets is 1-based
                    bucket = i + 1;
                }
            }

            _entries = entries;
        }

        public bool Remove(ReadOnlySpan<char> key)
        {
            using var _ = Write();

            if (_buckets != null)
            {
                Debug.Assert(_entries != null, "entries should be non-null");
                uint collisionCount = 0;

                uint hashCode = (uint)key.GetSpanHashCode();

                ref int bucket = ref GetBucket(hashCode);
                Entry[]? entries = _entries;
                int last = -1;
                int i = bucket - 1; // Value in buckets is 1-based
                while (i >= 0)
                {
                    ref Entry entry = ref entries[i];

                    if (entry.hashCode == hashCode && key.Equals(entry.key,StringComparison.Ordinal))
                    {
                        if (last < 0)
                        {
                            bucket = entry.next + 1; // Value in buckets is 1-based
                        }
                        else
                        {
                            entries[last].next = entry.next;
                        }

                        Debug.Assert((StartOfFreeList - _freeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
                        entry.next = StartOfFreeList - _freeList;
                                               
                        _freeList = i;
                        _freeCount++;
                        return true;
                    }

                    last = i;
                    i = entry.next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.

                        throw new InvalidOperationException("Concurrent operations not supported");
                    }
                }
            }
            return false;
        }

        public bool TryGetValue(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value)
        {
            using var _ = Read();

            ref TValue valRef = ref FindValue(key);
            if (!Unsafe.IsNullRef(ref valRef))
            {
                value = valRef;
                return true;
            }

            value = default;
            return false;
        }

        public bool TryAdd(ReadOnlySpan<char> key, TValue value) =>
            TryInsert(key, value, true);


      
        /// <summary>
        /// Ensures that the dictionary can hold up to 'capacity' entries without any further expansion of its backing storage
        /// </summary>
        public int EnsureCapacity(int capacity)
        {
            using var _ = Write();

            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            int currentCapacity = _entries == null ? 0 : _entries.Length;
            if (currentCapacity >= capacity)
            {
                return currentCapacity;
            }

            _version++;

            if (_buckets == null)
            {
                return Initialize(capacity);
            }

            int newSize = HashHelpers.GetPrime(capacity);
            Resize(newSize, forceNewHashCodes: false);
            return newSize;
        }

        /// <summary>
        /// Sets the capacity of this dictionary to what it would be if it had been originally initialized with all its entries
        /// </summary>
        /// <remarks>
        /// This method can be used to minimize the memory overhead
        /// once it is known that no new elements will be added.
        ///
        /// To allocate minimum size storage array, execute the following statements:
        ///
        /// dictionary.Clear();
        /// dictionary.TrimExcess();
        /// </remarks>
        public void TrimExcess() => TrimExcess(Count);

        /// <summary>
        /// Sets the capacity of this dictionary to hold up 'capacity' entries without any further expansion of its backing storage
        /// </summary>
        /// <remarks>
        /// This method can be used to minimize the memory overhead
        /// once it is known that no new elements will be added.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Passed capacity is lower than entries count.</exception>
        public void TrimExcess(int capacity)
        {
            using var _ = Write();

            if (capacity < Count)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            int newSize = HashHelpers.GetPrime(capacity);
            Entry[]? oldEntries = _entries;
            int currentCapacity = oldEntries == null ? 0 : oldEntries.Length;
            if (newSize >= currentCapacity)
            {
                return;
            }

            int oldCount = _count;
            _version++;
            Initialize(newSize);

            Debug.Assert(oldEntries is not null);

            CopyEntries(oldEntries, oldCount);
        }

        private void CopyEntries(Entry[] entries, int count)
        {
            Debug.Assert(_entries is not null);

            Entry[] newEntries = _entries;
            int newCount = 0;
            for (int i = 0; i < count; i++)
            {
                uint hashCode = entries[i].hashCode;
                if (entries[i].next >= -1)
                {
                    ref Entry entry = ref newEntries[newCount];
                    entry = entries[i];
                    ref int bucket = ref GetBucket(hashCode);
                    entry.next = bucket - 1; // Value in _buckets is 1-based
                    bucket = newCount + 1;
                    newCount++;
                }
            }

            _count = newCount;
            _freeCount = 0;
        }




        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref int GetBucket(uint hashCode)
        {
            int[] buckets = _buckets!;
#if TARGET_64BIT
            return ref buckets[HashHelpers.FastMod(hashCode, (uint)buckets.Length, _fastModMultiplier)];
#else
            return ref buckets[(uint)hashCode % buckets.Length];
#endif
        }

        private struct Entry
        {
            public uint hashCode;
            /// <summary>
            /// 0-based index of next entry in chain: -1 means end of chain
            /// also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
            /// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
            /// </summary>
            public int next;
            public string key;     // Key of entry
            public TValue value; // Value of entry
        }

        public struct Enumerator : IEnumerator<KeyValuePair<string, TValue>>, IDictionaryEnumerator
        {
            private readonly SpanDictionary<TValue> _dictionary;
            private readonly int _version;
            private int _index;
            private KeyValuePair<string, TValue> _current;
            private readonly int _getEnumeratorRetType;  // What should Enumerator.Current return?

            internal const int DictEntry = 1;
            internal const int KeyValuePair = 2;

            internal Enumerator(SpanDictionary<TValue> dictionary, int getEnumeratorRetType)
            {
                _dictionary = dictionary;
                _version = dictionary._version;
                _index = 0;
                _getEnumeratorRetType = getEnumeratorRetType;
                _current = default;
            }

            public bool MoveNext()
            {
                if (_version != _dictionary._version)
                {
                    throw new Exception();
                }

                // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
                // dictionary.count+1 could be negative if dictionary.count is int.MaxValue
                while ((uint)_index < (uint)_dictionary._count)
                {
                    ref Entry entry = ref _dictionary._entries![_index++];

                    if (entry.next >= -1)
                    {
                        _current = new KeyValuePair<string, TValue>(entry.key, entry.value);
                        return true;
                    }
                }

                _index = _dictionary._count + 1;
                _current = default;
                return false;
            }

            public KeyValuePair<string, TValue> Current => _current;

            public void Dispose() { }

            object? IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        throw new Exception();
                    }

                    if (_getEnumeratorRetType == DictEntry)
                    {
                        return new DictionaryEntry(_current.Key, _current.Value);
                    }

                    return new KeyValuePair<string, TValue>(_current.Key, _current.Value);
                }
            }

            void IEnumerator.Reset()
            {
                if (_version != _dictionary._version)
                {
                    throw new Exception();
                }

                _index = 0;
                _current = default;
            }

            DictionaryEntry IDictionaryEnumerator.Entry
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        throw new Exception();
                    }

                    return new DictionaryEntry(_current.Key, _current.Value);
                }
            }

            object IDictionaryEnumerator.Key
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        throw new Exception();
                    }

                    return _current.Key;
                }
            }

            object? IDictionaryEnumerator.Value
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        throw new Exception();
                    }

                    return _current.Value;
                }
            }
        }

        [DebuggerDisplay("Count = {Count}")]
        public sealed class KeyCollection
        {
            private readonly SpanDictionary<TValue> _dictionary;

            public KeyCollection(SpanDictionary<TValue> dictionary)
            {
                ArgumentNullException.ThrowIfNull(dictionary, nameof(dictionary));
                _dictionary = dictionary;
            }

            public Enumerator GetEnumerator() => new Enumerator(_dictionary);

            public void CopyTo(string[] array, int index)
            {
                if (array == null)
                {
                    ArgumentNullException.ThrowIfNull(array, nameof(array));
                }

                if (index < 0 || index > array.Length)
                {
                    throw new ArgumentOutOfRangeException();
                }

                if (array.Length - index < _dictionary.Count)
                {
                    throw new ArgumentOutOfRangeException();
                }

                int count = _dictionary._count;
                Entry[]? entries = _dictionary._entries;
                for (int i = 0; i < count; i++)
                {
                    if (entries![i].next >= -1) array[index++] = entries[i].key;
                }
            }

            public int Count => _dictionary.Count;

            public bool Contains(ReadOnlySpan<char> item) =>
                _dictionary.ContainsKey(item);

       
            public struct Enumerator : IEnumerator<string>, IEnumerator
            {
                private readonly SpanDictionary<TValue> _dictionary;
                private int _index;
                private readonly int _version;
                private string? _currentKey;

                internal Enumerator(SpanDictionary<TValue> dictionary)
                {
                    _dictionary = dictionary;
                    _version = dictionary._version;
                    _index = 0;
                    _currentKey = default;
                }

                public void Dispose() { }

                public bool MoveNext()
                {
                    if (_version != _dictionary._version)
                    {
                        throw new Exception();
                    }

                    while ((uint)_index < (uint)_dictionary._count)
                    {
                        ref Entry entry = ref _dictionary._entries![_index++];

                        if (entry.next >= -1)
                        {
                            _currentKey = entry.key;
                            return true;
                        }
                    }

                    _index = _dictionary._count + 1;
                    _currentKey = default;
                    return false;
                }

                public string Current => _currentKey!;

                object? IEnumerator.Current
                {
                    get
                    {
                        if (_index == 0 || (_index == _dictionary._count + 1))
                        {
                            throw new Exception();
                        }

                        return _currentKey;
                    }
                }

                void IEnumerator.Reset()
                {
                    if (_version != _dictionary._version)
                    {
                        throw new Exception();
                    }

                    _index = 0;
                    _currentKey = default;
                }
            }
        }

        public sealed class ValueCollection
        {
            private readonly SpanDictionary<TValue> _dictionary;

            public ValueCollection(SpanDictionary<TValue> dictionary)
            {
                if (dictionary == null)
                {
                    throw new Exception();
                }

                _dictionary = dictionary;
            }

            public Enumerator GetEnumerator() => new Enumerator(_dictionary);

            public void CopyTo(TValue[] array, int index)
            {
                if (array == null)
                {
                    throw new Exception();
                }

                if ((uint)index > array.Length)
                {
                    throw new Exception();
                }

                if (array.Length - index < _dictionary.Count)
                {
                    throw new Exception();
                }

                int count = _dictionary._count;
                Entry[]? entries = _dictionary._entries;
                for (int i = 0; i < count; i++)
                {
                    if (entries![i].next >= -1) array[index++] = entries[i].value;
                }
            }

            public int Count => _dictionary.Count;

         
            public struct Enumerator : IEnumerator<TValue>, IEnumerator
            {
                private readonly SpanDictionary<TValue> _dictionary;
                private int _index;
                private readonly int _version;
                private TValue? _currentValue;

                internal Enumerator(SpanDictionary<TValue> dictionary)
                {
                    _dictionary = dictionary;
                    _version = dictionary._version;
                    _index = 0;
                    _currentValue = default;
                }

                public void Dispose() { }

                public bool MoveNext()
                {
                    if (_version != _dictionary._version)
                    {
                        throw new Exception();
                    }

                    while ((uint)_index < (uint)_dictionary._count)
                    {
                        ref Entry entry = ref _dictionary._entries![_index++];

                        if (entry.next >= -1)
                        {
                            _currentValue = entry.value;
                            return true;
                        }
                    }
                    _index = _dictionary._count + 1;
                    _currentValue = default;
                    return false;
                }

                public TValue Current => _currentValue!;

                object? IEnumerator.Current
                {
                    get
                    {
                        if (_index == 0 || (_index == _dictionary._count + 1))
                        {
                            throw new Exception();
                        }

                        return _currentValue;
                    }
                }

                void IEnumerator.Reset()
                {
                    if (_version != _dictionary._version)
                    {
                        throw new Exception();
                    }

                    _index = 0;
                    _currentValue = default;
                }
            }
        }
    }
    internal static partial class HashHelpers
    {
        public const uint HashCollisionThreshold = 100;

        // This is the maximum prime smaller than Array.MaxLength.
        public const int MaxPrimeArrayLength = 0x7FFFFFC3;

        public const int HashPrime = 101;

        // Table of prime numbers to use as hash table sizes.
        // A typical resize algorithm would pick the smallest prime number in this array
        // that is larger than twice the previous capacity.
        // Suppose our Hashtable currently has capacity x and enough elements are added
        // such that a resize needs to occur. Resizing first computes 2x then finds the
        // first prime in the table greater than 2x, i.e. if primes are ordered
        // p_1, p_2, ..., p_i, ..., it finds p_n such that p_n-1 < 2x < p_n.
        // Doubling is important for preserving the asymptotic complexity of the
        // hashtable operations such as add.  Having a prime guarantees that double
        // hashing does not lead to infinite loops.  IE, your hash function will be
        // h1(key) + i*h2(key), 0 <= i < size.  h2 and the size must be relatively prime.
        // We prefer the low computation costs of higher prime numbers over the increased
        // memory allocation of a fixed prime number i.e. when right sizing a HashSet.
        internal static ReadOnlySpan<int> Primes => new int[]
        {
            3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521, 631, 761, 919,
            1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591,
            17519, 21023, 25229, 30293, 36353, 43627, 52361, 62851, 75431, 90523, 108631, 130363, 156437,
            187751, 225307, 270371, 324449, 389357, 467237, 560689, 672827, 807403, 968897, 1162687, 1395263,
            1674319, 2009191, 2411033, 2893249, 3471899, 4166287, 4999559, 5999471, 7199369
        };

        public static bool IsPrime(int candidate)
        {
            if ((candidate & 1) != 0)
            {
                int limit = (int)Math.Sqrt(candidate);
                for (int divisor = 3; divisor <= limit; divisor += 2)
                {
                    if ((candidate % divisor) == 0)
                        return false;
                }
                return true;
            }
            return candidate == 2;
        }

        public static int GetPrime(int min)
        {
            if (min < 0)
                throw new ArgumentException("Capacity overflow");

            foreach (int prime in Primes)
            {
                if (prime >= min)
                    return prime;
            }

            // Outside of our predefined table. Compute the hard way.
            for (int i = (min | 1); i < int.MaxValue; i += 2)
            {
                if (IsPrime(i) && ((i - 1) % HashPrime != 0))
                    return i;
            }
            return min;
        }

        // Returns size of hashtable to grow to.
        public static int ExpandPrime(int oldSize)
        {
            int newSize = 2 * oldSize;

            // Allow the hashtables to grow to maximum possible size (~2G elements) before encountering capacity overflow.
            // Note that this check works even when _items.Length overflowed thanks to the (uint) cast
            if ((uint)newSize > MaxPrimeArrayLength && MaxPrimeArrayLength > oldSize)
            {
                Debug.Assert(MaxPrimeArrayLength == GetPrime(MaxPrimeArrayLength), "Invalid MaxPrimeArrayLength");
                return MaxPrimeArrayLength;
            }

            return GetPrime(newSize);
        }

        /// <summary>Returns approximate reciprocal of the divisor: ceil(2**64 / divisor).</summary>
        /// <remarks>This should only be used on 64-bit.</remarks>
        public static ulong GetFastModMultiplier(uint divisor) =>
            ulong.MaxValue / divisor + 1;

        /// <summary>Performs a mod operation using the multiplier pre-computed with <see cref="GetFastModMultiplier"/>.</summary>
        /// <remarks>This should only be used on 64-bit.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint FastMod(uint value, uint divisor, ulong multiplier)
        {
            // We use modified Daniel Lemire's fastmod algorithm (https://github.com/dotnet/runtime/pull/406),
            // which allows to avoid the long multiplication if the divisor is less than 2**31.
            Debug.Assert(divisor <= int.MaxValue);

            // This is equivalent of (uint)Math.BigMul(multiplier * value, divisor, out _). This version
            // is faster than BigMul currently because we only need the high bits.
            uint highbits = (uint)(((((multiplier * value) >> 32) + 1) * divisor) >> 32);

            Debug.Assert(highbits == value % divisor);
            return highbits;
        }
    }

    internal static partial class MarvinSpanChar
    {
        internal static int GetSpanHashCode(this ReadOnlySpan<char> data)
        {
            int hash = MarvinSpanChar.ComputeHash32(
                                                    MemoryMarshal.AsBytes(data),   // ReadOnlySpan<byte> 视图
                                                    seed: MarvinSpanChar.DefaultSeed
                                                    );
            return hash;
        }
        /// <summary>
        /// Compute a Marvin hash and collapse it into a 32-bit hash.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComputeHash32(ReadOnlySpan<byte> data, ulong seed) => ComputeHash32(ref MemoryMarshal.GetReference(data), (uint)data.Length, (uint)seed, (uint)(seed >> 32));

        /// <summary>
        /// Compute a Marvin hash and collapse it into a 32-bit hash.
        /// </summary>
        private static int ComputeHash32(ref byte data, uint count, uint p0, uint p1)
        {
            // Control flow of this method generally flows top-to-bottom, trying to
            // minimize the number of branches taken for large (>= 8 bytes, 4 chars) inputs.
            // If small inputs (< 8 bytes, 4 chars) are given, this jumps to a "small inputs"
            // handler at the end of the method.

            if (count < 8)
            {
                // We can't run the main loop, but we might still have 4 or more bytes available to us.
                // If so, jump to the 4 .. 7 bytes logic immediately after the main loop.

                if (count >= 4)
                {
                    goto Between4And7BytesRemain;
                }
                else
                {
                    goto InputTooSmallToEnterMainLoop;
                }
            }

            // Main loop - read 8 bytes at a time.
            // The block function is unrolled 2x in this loop.

            uint loopCount = count / 8;
            Debug.Assert(loopCount > 0, "Shouldn't reach this code path for small inputs.");

            do
            {
                // Most x86 processors have two dispatch ports for reads, so we can read 2x 32-bit
                // values in parallel. We opt for this instead of a single 64-bit read since the
                // typical use case for Marvin32 is computing String hash codes, and the particular
                // layout of String instances means the starting data is never 8-byte aligned when
                // running in a 64-bit process.

                p0 += Unsafe.ReadUnaligned<uint>(ref data);
                uint nextUInt32 = Unsafe.ReadUnaligned<uint>(ref Unsafe.AddByteOffset(ref data, 4));

                // One block round for each of the 32-bit integers we just read, 2x rounds total.

                Block(ref p0, ref p1);
                p0 += nextUInt32;
                Block(ref p0, ref p1);

                // Bump the data reference pointer and decrement the loop count.

                // Decrementing by 1 every time and comparing against zero allows the JIT to produce
                // better codegen compared to a standard 'for' loop with an incrementing counter.
                // Requires https://github.com/dotnet/runtime/issues/6794 to be addressed first
                // before we can realize the full benefits of this.

                data = ref Unsafe.AddByteOffset(ref data, 8);
            } while (--loopCount > 0);

            // n.b. We've not been updating the original 'count' parameter, so its actual value is
            // still the original data length. However, we can still rely on its least significant
            // 3 bits to tell us how much data remains (0 .. 7 bytes) after the loop above is
            // completed.

            if ((count & 0b_0100) == 0)
            {
                goto DoFinalPartialRead;
            }

Between4And7BytesRemain:

// If after finishing the main loop we still have 4 or more leftover bytes, or if we had
// 4 .. 7 bytes to begin with and couldn't enter the loop in the first place, we need to
// consume 4 bytes immediately and send them through one round of the block function.

            Debug.Assert(count >= 4, "Only should've gotten here if the original count was >= 4.");

            p0 += Unsafe.ReadUnaligned<uint>(ref data);
            Block(ref p0, ref p1);

DoFinalPartialRead:

// Finally, we have 0 .. 3 bytes leftover. Since we know the original data length was at
// least 4 bytes (smaller lengths are handled at the end of this routine), we can safely
// read the 4 bytes at the end of the buffer without reading past the beginning of the
// original buffer. This necessarily means the data we're about to read will overlap with
// some data we've already processed, but we can handle that below.

            Debug.Assert(count >= 4, "Only should've gotten here if the original count was >= 4.");

            // Read the last 4 bytes of the buffer.

            uint partialResult = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref Unsafe.AddByteOffset(ref data, (nuint)count & 7), -4));

            // The 'partialResult' local above contains any data we have yet to read, plus some number
            // of bytes which we've already read from the buffer. An example of this is given below
            // for little-endian architectures. In this table, AA BB CC are the bytes which we still
            // need to consume, and ## are bytes which we want to throw away since we've already
            // consumed them as part of a previous read.
            //
            //                                                    (partialResult contains)   (we want it to contain)
            // count mod 4 = 0 -> [ ## ## ## ## |             ] -> 0x####_####             -> 0x0000_0080
            // count mod 4 = 1 -> [ ## ## ## ## | AA          ] -> 0xAA##_####             -> 0x0000_80AA
            // count mod 4 = 2 -> [ ## ## ## ## | AA BB       ] -> 0xBBAA_####             -> 0x0080_BBAA
            // count mod 4 = 3 -> [ ## ## ## ## | AA BB CC    ] -> 0xCCBB_AA##             -> 0x80CC_BBAA

            count = ~count << 3;

            if (BitConverter.IsLittleEndian)
            {
                partialResult >>= 8; // make some room for the 0x80 byte
                partialResult |= 0x8000_0000u; // put the 0x80 byte at the beginning
                partialResult >>= (int)count & 0x1F; // shift out all previously consumed bytes
            }
            else
            {
                partialResult <<= 8; // make some room for the 0x80 byte
                partialResult |= 0x80u; // put the 0x80 byte at the end
                partialResult <<= (int)count & 0x1F; // shift out all previously consumed bytes
            }

DoFinalRoundsAndReturn:

// Now that we've computed the final partial result, merge it in and run two rounds of
// the block function to finish out the Marvin algorithm.

            p0 += partialResult;
            Block(ref p0, ref p1);
            Block(ref p0, ref p1);

            return (int)(p1 ^ p0);

InputTooSmallToEnterMainLoop:

// We had only 0 .. 3 bytes to begin with, so we can't perform any 32-bit reads.
// This means that we're going to be building up the final result right away and
// will only ever run two rounds total of the block function. Let's initialize
// the partial result to "no data".

            if (BitConverter.IsLittleEndian)
            {
                partialResult = 0x80u;
            }
            else
            {
                partialResult = 0x80000000u;
            }

            if ((count & 0b_0001) != 0)
            {
                // If the buffer is 1 or 3 bytes in length, let's read a single byte now
                // and merge it into our partial result. This will result in partialResult
                // having one of the two values below, where AA BB CC are the buffer bytes.
                //
                //                  (little-endian / big-endian)
                // [ AA          ]  -> 0x0000_80AA / 0xAA80_0000
                // [ AA BB CC    ]  -> 0x0000_80CC / 0xCC80_0000

                partialResult = Unsafe.AddByteOffset(ref data, (nuint)count & 2);

                if (BitConverter.IsLittleEndian)
                {
                    partialResult |= 0x8000;
                }
                else
                {
                    partialResult <<= 24;
                    partialResult |= 0x800000u;
                }
            }

            if ((count & 0b_0010) != 0)
            {
                // If the buffer is 2 or 3 bytes in length, let's read a single ushort now
                // and merge it into the partial result. This will result in partialResult
                // having one of the two values below, where AA BB CC are the buffer bytes.
                //
                //                  (little-endian / big-endian)
                // [ AA BB       ]  -> 0x0080_BBAA / 0xAABB_8000
                // [ AA BB CC    ]  -> 0x80CC_BBAA / 0xAABB_CC80 (carried over from above)

                if (BitConverter.IsLittleEndian)
                {
                    partialResult <<= 16;
                    partialResult |= (uint)Unsafe.ReadUnaligned<ushort>(ref data);
                }
                else
                {
                    partialResult |= (uint)Unsafe.ReadUnaligned<ushort>(ref data);
                    partialResult = BitOperations.RotateLeft(partialResult, 16);
                }
            }

            // Everything is consumed! Go perform the final rounds and return.

            goto DoFinalRoundsAndReturn;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Block(ref uint rp0, ref uint rp1)
        {
            // Intrinsified in mono interpreter
            uint p0 = rp0;
            uint p1 = rp1;

            p1 ^= p0;
            p0 = BitOperations.RotateLeft(p0, 20);

            p0 += p1;
            p1 = BitOperations.RotateLeft(p1, 9);

            p1 ^= p0;
            p0 = BitOperations.RotateLeft(p0, 27);

            p0 += p1;
            p1 = BitOperations.RotateLeft(p1, 19);

            rp0 = p0;
            rp1 = p1;
        }

        private static ulong DefaultSeed = 0;
    }

}
