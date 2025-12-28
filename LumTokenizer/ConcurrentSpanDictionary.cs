using global::System.Collections.ObjectModel;
using System;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Xml.Linq;


namespace LumTokenizer
{

    public class ConcurrentSpanDictionary<TValue>: IEnumerable
    {
        /// <summary>Internal tables of the dictionary.</summary>
        /// <remarks>
        /// When using <see cref="_tables"/>, we must read the volatile _tables field into a local variable:
        /// it is set to a new table on each table resize. Volatile.Reads on array elements then ensure that
        /// we have a copy of the reference to tables._buckets[bucketNo]: this protects us from reading fields
        /// ('_hashcode', '_key', '_value' and '_next') of different instances.
        /// </remarks>
        private volatile Tables _tables;
        /// <summary>The maximum number of elements per lock before a resize operation is triggered.</summary>
        private int _budget;
        /// <summary>Whether to dynamically increase the size of the striped lock.</summary>
        private readonly bool _growLockArray;
        /// <summary>Whether a non-null comparer in <see cref="Tables._comparer"/> is the default comparer.</summary>
        /// <remarks>
        /// This is only used for reference types. It lets us use the key's GetHashCode directly rather than going indirectly
        /// through the comparer.  It can't be used for Equals, as the key might implement IEquatable and employ different
        /// equality semantics than the virtual Equals, however unlikely that may be. This field enables us to save an
        /// interface dispatch when using the default comparer with a non-string reference type key, at the expense of an
        /// extra branch when using a custom comparer with a reference type key.
        /// </remarks>
        private readonly bool _comparerIsDefaultForClasses;

        /// <summary>The default capacity, i.e. the initial # of buckets.</summary>
        /// <remarks>
        /// When choosing this value, we are making a trade-off between the size of a very small dictionary,
        /// and the number of resizes when constructing a large dictionary.
        /// </remarks>
        private const int DefaultCapacity = 31;

        /// <summary>
        /// The maximum size of the striped lock that will not be exceeded when locks are automatically
        /// added as the dictionary grows.
        /// </summary>
        /// <remarks>
        /// The user is allowed to exceed this limit by passing
        /// a concurrency level larger than MaxLockNumber into the constructor.
        /// </remarks>
        private const int MaxLockNumber = 1024;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionary{string,TValue}"/>
        /// class that is empty, has the default concurrency level, has the default initial capacity, and
        /// uses the default comparer for the key type.
        /// </summary>
        public ConcurrentSpanDictionary()
            : this(DefaultConcurrencyLevel, DefaultCapacity, growLockArray: true) { }


        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionary{string,TValue}"/>
        /// class that is empty, has the specified concurrency level, has the specified initial capacity, and
        /// uses the specified <see cref="IEqualityComparer{string}"/>.
        /// </summary>
        /// <param name="concurrencyLevel">The estimated number of threads that will update the <see cref="ConcurrentDictionary{string,TValue}"/> concurrently, or -1 to indicate a default value.</param>
        /// <param name="capacity">The initial number of elements that the <see cref="ConcurrentDictionary{string,TValue}"/> can contain.</param>
        /// <param name="comparer">The <see cref="IEqualityComparer{string}"/> implementation to use when comparing keys.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="concurrencyLevel"/> is less than 1. -or- <paramref name="capacity"/> is less than 0.</exception>
        public ConcurrentSpanDictionary(int concurrencyLevel, int capacity)
            : this(concurrencyLevel, capacity, growLockArray: false)
        {
        }

        public ConcurrentSpanDictionary(int capacity)
            : this(DefaultConcurrencyLevel, capacity, growLockArray: false)
        {
        }

        private ConcurrentSpanDictionary(int concurrencyLevel, int capacity, bool growLockArray)
        {
            if (concurrencyLevel <= 0)
            {
                if (concurrencyLevel != -1)
                {
                    throw new ArgumentOutOfRangeException(nameof(concurrencyLevel));
                }

                concurrencyLevel = DefaultConcurrencyLevel;
            }

            ArgumentOutOfRangeException.ThrowIfNegative(capacity);

            // The capacity should be at least as large as the concurrency level. Otherwise, we would have locks that don't guard
            // any buckets.  We also want it to be a prime.
            if (capacity < concurrencyLevel)
            {
                capacity = concurrencyLevel;
            }
            capacity = HashHelpers.GetPrime(capacity);

            var locks = new object[concurrencyLevel];
            locks[0] = locks; // reuse array as the first lock object just to avoid an additional allocation
            for (int i = 1; i < locks.Length; i++)
            {
                locks[i] = new object();
            }

            var countPerLock = new int[locks.Length];
            var buckets = new VolatileNode[capacity];



            _tables = new Tables(buckets, locks, countPerLock);
            _growLockArray = growLockArray;
            _budget = buckets.Length / locks.Length;
        }


        /// <summary>Computes the hash code for the specified key using the dictionary's comparer.</summary>
        /// <param name="comparer">
        /// The comparer. It's passed in to avoid having to look it up via a volatile read on <see cref="_tables"/>;
        /// such a comparer could also be incorrect if the table upgraded comparer concurrently.
        /// </param>
        /// <param name="key">The key for which to compute the hash code.</param>
        /// <returns>The hash code of the key.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetHashCode(ReadOnlySpan<char> key)
        {
            return key.GetSpanHashCode();
        }

        /// <summary>Determines whether the specified key and the key stored in the specified node are equal.</summary>
        /// <param name="comparer">
        /// The comparer. It's passed in to avoid having to look it up via a volatile read on <see cref="_tables"/>;
        /// such a comparer could also be incorrect if the table upgraded comparer concurrently.
        /// </param>
        /// <param name="node">The node containing the key to compare.</param>
        /// <param name="key">The other key to compare.</param>
        /// <returns>true if the keys are equal; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NodeEqualsKey(Node node, ReadOnlySpan<char> key)
        {
            return SpanCharHelper.SequenceEqual(node._key, key);
        }


        /// <summary>
        /// Attempts to add the specified key and value to the <see cref="ConcurrentDictionary{string, TValue}"/>.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="value">The value of the element to add. The value can be a null reference (Nothing
        /// in Visual Basic) for reference types.</param>
        /// <returns>
        /// true if the key/value pair was added to the <see cref="ConcurrentDictionary{string, TValue}"/> successfully; otherwise, false.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is null reference (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The <see cref="ConcurrentDictionary{string, TValue}"/> contains too many elements.</exception>
        public bool TryAdd(ReadOnlySpan<char> key, TValue value)
        {
            return TryAddInternal(_tables, key, value, updateIfExists: false, acquireLock: true, out _);
        }

        /// <summary>
        /// Determines whether the <see cref="ConcurrentDictionary{string, TValue}"/> contains the specified key.
        /// </summary>
        /// <param name="key">The key to locate in the <see cref="ConcurrentDictionary{string, TValue}"/>.</param>
        /// <returns>true if the <see cref="ConcurrentDictionary{string, TValue}"/> contains an element with the specified key; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference (Nothing in Visual Basic).</exception>
        public bool ContainsKey(string key) => TryGetValue(key, out _);

        /// <summary>
        /// Attempts to remove and return the value with the specified key from the <see cref="ConcurrentDictionary{string, TValue}"/>.
        /// </summary>
        /// <param name="key">The key of the element to remove and return.</param>
        /// <param name="value">
        /// When this method returns, <paramref name="value"/> contains the object removed from the
        /// <see cref="ConcurrentDictionary{string,TValue}"/> or the default value of <typeparamref
        /// name="TValue"/> if the operation failed.
        /// </param>
        /// <returns>true if an object was removed successfully; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference (Nothing in Visual Basic).</exception>
        public bool TryRemove(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value)
        {
            return TryRemoveInternal(key, out value, matchValue: false, default);
        }

        /// <summary>Removes a key and value from the dictionary.</summary>
        /// <param name="item">The <see cref="KeyValuePair{string,TValue}"/> representing the key and value to remove.</param>
        /// <returns>
        /// true if the key and value represented by <paramref name="item"/> are successfully
        /// found and removed; otherwise, false.
        /// </returns>
        /// <remarks>
        /// Both the specified key and value must match the entry in the dictionary for it to be removed.
        /// The key is compared using the dictionary's comparer (or the default comparer for <typeparamref name="string"/>
        /// if no comparer was provided to the dictionary when it was constructed).  The value is compared using the
        /// default comparer for <typeparamref name="TValue"/>.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// The <see cref="KeyValuePair{string, TValue}.Key"/> property of <paramref name="item"/> is a null reference.
        /// </exception>
        public bool TryRemove(KeyValuePair<string, TValue> item)
        {
            return TryRemoveInternal(item.Key, out _, matchValue: true, item.Value);
        }

        /// <summary>
        /// Removes the specified key from the dictionary if it exists and returns its associated value.
        /// If matchValue flag is set, the key will be removed only if is associated with a particular
        /// value.
        /// </summary>
        /// <param name="key">The key to search for and remove if it exists.</param>
        /// <param name="value">The variable into which the removed value, if found, is stored.</param>
        /// <param name="matchValue">Whether removal of the key is conditional on its value.</param>
        /// <param name="oldValue">The conditional value to compare against if <paramref name="matchValue"/> is true</param>
        private bool TryRemoveInternal(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value, bool matchValue, TValue? oldValue)
        {
            Tables tables = _tables;

            int hashcode = GetHashCode(key);

            while (true)
            {
                object[] locks = tables._locks;
                ref Node? bucket = ref GetBucketAndLock(tables, hashcode, out uint lockNo);

                lock (locks[lockNo])
                {
                    // If the table just got resized, we may not be holding the right lock, and must retry.
                    // This should be a rare occurrence.
                    if (tables != _tables)
                    {
                        tables = _tables;
                        hashcode = GetHashCode(key);
                        continue;
                    }

                    Node? prev = null;
                    for (Node? curr = bucket; curr is not null; curr = curr._next)
                    {
                        Debug.Assert((prev is null && curr == bucket) || prev!._next == curr);

                        if (hashcode == curr._hashcode && NodeEqualsKey(curr, key))
                        {
                            if (matchValue)
                            {
                                bool valuesMatch = EqualityComparer<TValue>.Default.Equals(oldValue, curr._value);
                                if (!valuesMatch)
                                {
                                    value = default;
                                    return false;
                                }
                            }

                            if (prev is null)
                            {
                                Volatile.Write(ref bucket, curr._next);
                            }
                            else
                            {
                                prev._next = curr._next;
                            }

                            value = curr._value;
                            tables._countPerLock[lockNo]--;
                            return true;
                        }
                        prev = curr;
                    }
                }

                value = default;
                return false;
            }
        }

        /// <summary>
        /// Attempts to get the value associated with the specified key from the <see cref="ConcurrentDictionary{string,TValue}"/>.
        /// </summary>
        /// <param name="key">The key of the value to get.</param>
        /// <param name="value">
        /// When this method returns, <paramref name="value"/> contains the object from
        /// the <see cref="ConcurrentDictionary{string,TValue}"/> with the specified key or the default value of
        /// <typeparamref name="TValue"/>, if the operation failed.
        /// </param>
        /// <returns>true if the key was found in the <see cref="ConcurrentDictionary{string,TValue}"/>; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference (Nothing in Visual Basic).</exception>
        public bool TryGetValue(ReadOnlySpan<char> key, [MaybeNullWhen(false)] out TValue value)
        {
            Tables tables = _tables;

            int hashcode = key.GetSpanHashCode();
            for (Node? n = GetBucket(tables, hashcode); n is not null; n = n._next)
            {
                if (hashcode == n._hashcode && SpanCharHelper.SequenceEqual(n._key, key))
                {
                    value = n._value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private static bool TryGetValueInternal(Tables tables, string key, int hashcode, [MaybeNullWhen(false)] out TValue value)
        {
            for (Node? n = GetBucket(tables, hashcode); n is not null; n = n._next)
            {
                if (hashcode == n._hashcode && EqualityComparer<string>.Default.Equals(n._key, key))
                {
                    value = n._value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Updates the value associated with <paramref name="key"/> to <paramref name="newValue"/> if the existing value is equal
        /// to <paramref name="comparisonValue"/>.
        /// </summary>
        /// <param name="key">The key whose value is compared with <paramref name="comparisonValue"/> and
        /// possibly replaced.</param>
        /// <param name="newValue">The value that replaces the value of the element with <paramref
        /// name="key"/> if the comparison results in equality.</param>
        /// <param name="comparisonValue">The value that is compared to the value of the element with
        /// <paramref name="key"/>.</param>
        /// <returns>
        /// true if the value with <paramref name="key"/> was equal to <paramref name="comparisonValue"/> and
        /// replaced with <paramref name="newValue"/>; otherwise, false.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
        public bool TryUpdate(string key, TValue newValue, TValue comparisonValue)
        {
            return TryUpdateInternal(_tables, key, null, newValue, comparisonValue);
        }

        /// <summary>
        /// Updates the value associated with <paramref name="key"/> to <paramref name="newValue"/> if the existing value is equal
        /// to <paramref name="comparisonValue"/>.
        /// </summary>
        /// <param name="tables">The tables that were used to create the hash code.</param>
        /// <param name="key">The key whose value is compared with <paramref name="comparisonValue"/> and
        /// possibly replaced.</param>
        /// <param name="nullableHashcode">The hashcode computed for <paramref name="key"/>.</param>
        /// <param name="newValue">The value that replaces the value of the element with <paramref
        /// name="key"/> if the comparison results in equality.</param>
        /// <param name="comparisonValue">The value that is compared to the value of the element with
        /// <paramref name="key"/>.</param>
        /// <returns>
        /// true if the value with <paramref name="key"/> was equal to <paramref name="comparisonValue"/> and
        /// replaced with <paramref name="newValue"/>; otherwise, false.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
        private bool TryUpdateInternal(Tables tables, string key, int? nullableHashcode, TValue newValue, TValue comparisonValue)
        {

            int hashcode = nullableHashcode ?? GetHashCode(key);
            Debug.Assert(nullableHashcode is null || nullableHashcode == hashcode);

            EqualityComparer<TValue> valueComparer = EqualityComparer<TValue>.Default;

            while (true)
            {
                object[] locks = tables._locks;
                ref Node? bucket = ref GetBucketAndLock(tables, hashcode, out uint lockNo);

                lock (locks[lockNo])
                {
                    // If the table just got resized, we may not be holding the right lock, and must retry.
                    // This should be a rare occurrence.
                    if (tables != _tables)
                    {
                        tables = _tables;
                        hashcode = GetHashCode(key);
                        continue;
                    }

                    // Try to find this key in the bucket
                    Node? prev = null;
                    for (Node? node = bucket; node is not null; node = node._next)
                    {
                        Debug.Assert((prev is null && node == bucket) || prev!._next == node);
                        if (hashcode == node._hashcode && NodeEqualsKey(node, key))
                        {
                            if (valueComparer.Equals(node._value, comparisonValue))
                            {
                                // Do the reference type check up front to handle many cases of shared generics.
                                // If TValue is a value type then the field's value here can be baked in. Otherwise,
                                // for the remaining shared generic cases the field access here would disqualify inlining,
                                // so the following check cannot be factored out of TryAddInternal/TryUpdateInternal.
                                if (!typeof(TValue).IsValueType || ConcurrentDictionaryTypeProps<TValue>.IsWriteAtomic)
                                {
                                    node._value = newValue;
                                }
                                else
                                {
                                    var newNode = new Node(node._key, newValue, hashcode, node._next);

                                    if (prev is null)
                                    {
                                        Volatile.Write(ref bucket, newNode);
                                    }
                                    else
                                    {
                                        prev._next = newNode;
                                    }
                                }

                                return true;
                            }

                            return false;
                        }

                        prev = node;
                    }

                    // didn't find the key
                    return false;
                }
            }
        }

        /// <summary>
        /// Removes all keys and values from the <see cref="ConcurrentDictionary{string,TValue}"/>.
        /// </summary>
        public void Clear()
        {
            int locksAcquired = 0;
            try
            {
                AcquireAllLocks(ref locksAcquired);

                // If the dictionary is already empty, then there's nothing to clear.
                if (AreAllBucketsEmpty())
                {
                    return;
                }

                Tables tables = _tables;
                var newTables = new Tables(new VolatileNode[HashHelpers.GetPrime(DefaultCapacity)], tables._locks, new int[tables._countPerLock.Length]);
                _tables = newTables;
                _budget = Math.Max(1, newTables._buckets.Length / newTables._locks.Length);
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

       
        /// <summary>
        /// Copies the key and value pairs stored in the <see cref="ConcurrentDictionary{string,TValue}"/> to a
        /// new array.
        /// </summary>
        /// <returns>A new array containing a snapshot of key and value pairs copied from the <see cref="ConcurrentDictionary{string,TValue}"/>.
        /// </returns>
        public KeyValuePair<string, TValue>[] ToArray()
        {
            int locksAcquired = 0;
            try
            {
                AcquireAllLocks(ref locksAcquired);

                int count = GetCountNoLocks();
                if (count == 0)
                {
                    return Array.Empty<KeyValuePair<string, TValue>>();
                }

                var array = new KeyValuePair<string, TValue>[count];
                CopyToPairs(array, 0);
                return array;
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        /// <summary>Copy dictionary contents to an array - shared implementation between ToArray and CopyTo.</summary>
        /// <remarks>Important: the caller must hold all locks in _locks before calling CopyToPairs.</remarks>
        private void CopyToPairs(KeyValuePair<string, TValue>[] array, int index)
        {
            foreach (VolatileNode bucket in _tables._buckets)
            {
                for (Node? current = bucket._node; current is not null; current = current._next)
                {
                    array[index] = new KeyValuePair<string, TValue>(current._key, current._value);
                    Debug.Assert(index < int.MaxValue, "This method should only be called when there's no overflow risk");
                    index++;
                }
            }
        }

        /// <summary>Copy dictionary contents to an array - shared implementation between ToArray and CopyTo.</summary>
        /// <remarks>Important: the caller must hold all locks in _locks before calling CopyToPairs.</remarks>
        private void CopyToEntries(DictionaryEntry[] array, int index)
        {
            foreach (VolatileNode bucket in _tables._buckets)
            {
                for (Node? current = bucket._node; current is not null; current = current._next)
                {
                    array[index] = new DictionaryEntry(current._key, current._value);
                    Debug.Assert(index < int.MaxValue, "This method should only be called when there's no overflow risk");
                    index++;
                }
            }
        }

        /// <summary>Copy dictionary contents to an array - shared implementation between ToArray and CopyTo.</summary>
        /// <remarks>Important: the caller must hold all locks in _locks before calling CopyToPairs.</remarks>
        private void CopyToObjects(object[] array, int index)
        {
            foreach (VolatileNode bucket in _tables._buckets)
            {
                for (Node? current = bucket._node; current is not null; current = current._next)
                {
                    array[index] = new KeyValuePair<string, TValue>(current._key, current._value);
                    Debug.Assert(index < int.MaxValue, "This method should only be called when there's no overflow risk");
                    index++;
                }
            }
        }

        /// <summary>Returns an enumerator that iterates through the <see
        /// cref="ConcurrentDictionary{string,TValue}"/>.</summary>
        /// <returns>An enumerator for the <see cref="ConcurrentDictionary{string,TValue}"/>.</returns>
        /// <remarks>
        /// The enumerator returned from the dictionary is safe to use concurrently with
        /// reads and writes to the dictionary, however it does not represent a moment-in-time snapshot
        /// of the dictionary.  The contents exposed through the enumerator may contain modifications
        /// made to the dictionary after <see cref="GetEnumerator"/> was called.
        /// </remarks>
        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => new Enumerator(this);

        /// <summary>Provides an enumerator implementation for the dictionary.</summary>
        private sealed class Enumerator : IEnumerator<KeyValuePair<string, TValue>>
        {
            // Provides a manually-implemented version of (approximately) this iterator:
            //     VolatileNodeWrapper[] buckets = _tables._buckets;
            //     for (int i = 0; i < buckets.Length; i++)
            //         for (Node? current = buckets[i]._node; current is not null; current = current._next)
            //             yield return new KeyValuePair<string, TValue>(current._key, current._value);

            private readonly ConcurrentSpanDictionary<TValue> _dictionary;

            private ConcurrentSpanDictionary<TValue>.VolatileNode[]? _buckets;
            private Node? _node;
            private int _i;
            private int _state;

            private const int StateUninitialized = 0;
            private const int StateOuterloop = 1;
            private const int StateInnerLoop = 2;
            private const int StateDone = 3;

            public Enumerator(ConcurrentSpanDictionary<TValue> dictionary)
            {
                _dictionary = dictionary;
                _i = -1;
            }

            public KeyValuePair<string, TValue> Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Reset()
            {
                _buckets = null;
                _node = null;
                Current = default;
                _i = -1;
                _state = StateUninitialized;
            }

            public void Dispose() { }

            public bool MoveNext()
            {
                switch (_state)
                {
                    case StateUninitialized:
                        _buckets = _dictionary._tables._buckets;
                        _i = -1;
                        goto case StateOuterloop;

                    case StateOuterloop:
                        ConcurrentSpanDictionary<TValue>.VolatileNode[]? buckets = _buckets;
                        Debug.Assert(buckets is not null);

                        int i = ++_i;
                        if ((uint)i < (uint)buckets.Length)
                        {
                            _node = buckets[i]._node;
                            _state = StateInnerLoop;
                            goto case StateInnerLoop;
                        }
                        goto default;

                    case StateInnerLoop:
                        if (_node is Node node)
                        {
                            Current = new KeyValuePair<string, TValue>(node._key, node._value);
                            _node = node._next;
                            return true;
                        }
                        goto case StateOuterloop;

                    default:
                        _state = StateDone;
                        return false;
                }
            }
        }

        /// <summary>
        /// Shared internal implementation for inserts and updates.
        /// If key exists, we always return false; and if updateIfExists == true we force update with value;
        /// If key doesn't exist, we always add value and return true;
        /// </summary>
        private bool TryAddInternal(Tables tables, ReadOnlySpan<char> key, TValue value, bool updateIfExists, bool acquireLock, out TValue resultingValue)
        {            
            int hashcode = GetHashCode(key);

            while (true)
            {
                object[] locks = tables._locks;
                ref Node? bucket = ref GetBucketAndLock(tables, hashcode, out uint lockNo);

                bool resizeDesired = false;
                bool lockTaken = false;
                try
                {
                    if (acquireLock)
                    {
                        Monitor.Enter(locks[lockNo], ref lockTaken);
                    }

                    // If the table just got resized, we may not be holding the right lock, and must retry.
                    // This should be a rare occurrence.
                    if (tables != _tables)
                    {
                        tables = _tables;
                        hashcode = GetHashCode(key);
                        continue;
                    }

                    // Try to find this key in the bucket
                    uint collisionCount = 0;
                    Node? prev = null;
                    for (Node? node = bucket; node is not null; node = node._next)
                    {
                        Debug.Assert((prev is null && node == bucket) || prev!._next == node);
                        if (hashcode == node._hashcode && NodeEqualsKey(node, key))
                        {
                            // The key was found in the dictionary. If updates are allowed, update the value for that key.
                            // We need to create a new node for the update, in order to support TValue types that cannot
                            // be written atomically, since lock-free reads may be happening concurrently.
                            if (updateIfExists)
                            {
                                // Do the reference type check up front to handle many cases of shared generics.
                                // If TValue is a value type then the field's value here can be baked in. Otherwise,
                                // for the remaining shared generic cases the field access here would disqualify inlining,
                                // so the following check cannot be factored out of TryAddInternal/TryUpdateInternal.
                                if (!typeof(TValue).IsValueType || ConcurrentDictionaryTypeProps<TValue>.IsWriteAtomic)
                                {
                                    node._value = value;
                                }
                                else
                                {
                                    var newNode = new Node(node._key, value, hashcode, node._next);
                                    if (prev is null)
                                    {
                                        Volatile.Write(ref bucket, newNode);
                                    }
                                    else
                                    {
                                        prev._next = newNode;
                                    }
                                }
                                resultingValue = value;
                            }
                            else
                            {
                                resultingValue = node._value;
                            }
                            return false;
                        }
                        prev = node;
                        if (!typeof(string).IsValueType) // this is only relevant to strings, and we can avoid this code for all value types
                        {
                            collisionCount++;
                        }
                    }

                    // The key was not found in the bucket. Insert the key-value pair.
                    var resultNode = new Node(key.ToString(), value, hashcode, bucket);
                    Volatile.Write(ref bucket, resultNode);
                    checked
                    {
                        tables._countPerLock[lockNo]++;
                    }

                    // If the number of elements guarded by this lock has exceeded the budget, resize the bucket table.
                    // It is also possible that GrowTable will increase the budget but won't resize the bucket table.
                    // That happens if the bucket table is found to be poorly utilized due to a bad hash function.
                    if (tables._countPerLock[lockNo] > _budget)
                    {
                        resizeDesired = true;
                    }
                }
                finally
                {
                    if (lockTaken)
                    {
                        Monitor.Exit(locks[lockNo]);
                    }
                }

                // The fact that we got here means that we just performed an insertion. If necessary, we will grow the table.
                //
                // Concurrency notes:
                // - Notice that we are not holding any locks at when calling GrowTable. This is necessary to prevent deadlocks.
                // - As a result, it is possible that GrowTable will be called unnecessarily. But, GrowTable will obtain lock 0
                //   and then verify that the table we passed to it as the argument is still the current table.
                if (resizeDesired)
                {
                    GrowTable(tables, resizeDesired);
                }

                resultingValue = value;
                return true;
            }
        }

        /// <summary>Gets or sets the value associated with the specified key.</summary>
        /// <param name="key">The key of the value to get or set.</param>
        /// <value>
        /// The value associated with the specified key. If the specified key is not found, a get operation throws a
        /// <see cref="KeyNotFoundException"/>, and a set operation creates a new element with the specified key.
        /// </value>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="key"/> is a null reference (Nothing in Visual Basic).
        /// </exception>
        /// <exception cref="KeyNotFoundException">
        /// The property is retrieved and <paramref name="key"/> does not exist in the collection.
        /// </exception>
        public TValue this[ReadOnlySpan<char> key]
        {
            get
            {
                if (!TryGetValue(key, out TValue? value))
                {
                    ThrowKeyNotFoundException(key);
                }
                return value;
            }
            set
            {
                    TryAddInternal(_tables, key, value, updateIfExists: true, acquireLock: true, out _);
            }
        }

        /// <summary>Throws a KeyNotFoundException.</summary>
        /// <remarks>Separate from ThrowHelper to avoid boxing at call site while reusing this generic instantiation.</remarks>
        [DoesNotReturn]
        private static void ThrowKeyNotFoundException(ReadOnlySpan<char> key) =>
            throw new KeyNotFoundException(key.ToString());

       

        /// <summary>
        /// Gets the number of key/value pairs contained in the <see
        /// cref="ConcurrentDictionary{string,TValue}"/>.
        /// </summary>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <value>The number of key/value pairs contained in the <see
        /// cref="ConcurrentDictionary{string,TValue}"/>.</value>
        /// <remarks>Count has snapshot semantics and represents the number of items in the <see
        /// cref="ConcurrentDictionary{string,TValue}"/>
        /// at the moment when Count was accessed.</remarks>
        public int Count
        {
            get
            {
                int locksAcquired = 0;
                try
                {
                    AcquireAllLocks(ref locksAcquired);

                    return GetCountNoLocks();
                }
                finally
                {
                    ReleaseLocks(locksAcquired);
                }
            }
        }

        /// <summary>Gets the number of pairs stored in the dictionary.</summary>
        /// <remarks>This assumes all of the dictionary's locks have been taken, or else the result may not be accurate.</remarks>
        private int GetCountNoLocks()
        {
            int count = 0;
            foreach (int value in _tables._countPerLock)
            {
                checked { count += value; }
            }

            return count;
        }

        
        /// <summary>
        /// Gets a value that indicates whether the <see cref="ConcurrentDictionary{string,TValue}"/> is empty.
        /// </summary>
        /// <value>true if the <see cref="ConcurrentDictionary{string,TValue}"/> is empty; otherwise,
        /// false.</value>
        public bool IsEmpty
        {
            get
            {
                // Check if any buckets are non-empty, without acquiring any locks.
                // This fast path should generally suffice as collections are usually not empty.
                if (!AreAllBucketsEmpty())
                {
                    return false;
                }

                // We didn't see any buckets containing items, however we can't be sure
                // the collection was actually empty at any point in time as items may have been
                // added and removed while iterating over the buckets such that we never saw an
                // empty bucket, but there was always an item present in at least one bucket.
                int locksAcquired = 0;
                try
                {
                    AcquireAllLocks(ref locksAcquired);

                    return AreAllBucketsEmpty();
                }
                finally
                {
                    ReleaseLocks(locksAcquired);
                }
            }
        }

        #region IEnumerable Members

        /// <summary>Returns an enumerator that iterates through the <see
        /// cref="ConcurrentDictionary{string,TValue}"/>.</summary>
        /// <returns>An enumerator for the <see cref="ConcurrentDictionary{string,TValue}"/>.</returns>
        /// <remarks>
        /// The enumerator returned from the dictionary is safe to use concurrently with
        /// reads and writes to the dictionary, however it does not represent a moment-in-time snapshot
        /// of the dictionary.  The contents exposed through the enumerator may contain modifications
        /// made to the dictionary after <see cref="GetEnumerator"/> was called.
        /// </remarks>
        IEnumerator IEnumerable.GetEnumerator() => ((ConcurrentSpanDictionary<TValue>)this).GetEnumerator();

        #endregion

        private bool AreAllBucketsEmpty() =>
            !_tables._countPerLock.AsSpan().ContainsAnyExcept(0);

        /// <summary>
        /// Replaces the bucket table with a larger one. To prevent multiple threads from resizing the
        /// table as a result of races, the Tables instance that holds the table of buckets deemed too
        /// small is passed in as an argument to GrowTable(). GrowTable() obtains a lock, and then checks
        /// the Tables instance has been replaced in the meantime or not.
        /// </summary>
        private void GrowTable(Tables tables, bool resizeDesired)
        {
            int locksAcquired = 0;
            try
            {
                // The thread that first obtains _locks[0] will be the one doing the resize operation
                AcquireFirstLock(ref locksAcquired);

                // Make sure nobody resized the table while we were waiting for lock 0:
                if (tables != _tables)
                {
                    // We assume that since the table reference is different, it was already resized (or the budget
                    // was adjusted). If we ever decide to do table shrinking, or replace the table for other reasons,
                    // we will have to revisit this logic.
                    return;
                }

                int newLength = tables._buckets.Length;

             
                if (resizeDesired)
                {
                    // Compute the (approx.) total size. Use an Int64 accumulation variable to avoid an overflow.
                    // If the bucket array is too empty, we have an imbalance.
                    // If we have a string key and we're still using a non-randomized comparer,
                    // take this as a sign that we need to upgrade to one.
                    // Otherwise, double the budget instead of resizing the table.
                    if (GetCountNoLocks() < tables._buckets.Length / 4)
                    {
                        _budget = 2 * _budget;
                        if (_budget < 0)
                        {
                            _budget = int.MaxValue;
                        }
                        return;
                    }

                    // Compute the new table size at least twice the previous table size.
                    // Double the size of the buckets table and choose a prime that's at least as large.
                    if ((newLength = tables._buckets.Length * 2) < 0 ||
                        (newLength = HashHelpers.GetPrime(newLength)) > Array.MaxLength)
                    {
                        newLength = Array.MaxLength;

                        // We want to make sure that GrowTable will not be called again, since table is at the maximum size.
                        // To achieve that, we set the budget to int.MaxValue.
                        //
                        // (There is one special case that would allow GrowTable() to be called in the future:
                        // calling Clear() on the ConcurrentDictionary will shrink the table and lower the budget.)
                        _budget = int.MaxValue;
                    }
                }

                object[] newLocks = tables._locks;

                // Add more locks
                if (_growLockArray && tables._locks.Length < MaxLockNumber)
                {
                    newLocks = new object[tables._locks.Length * 2];
                    Array.Copy(tables._locks, newLocks, tables._locks.Length);
                    for (int i = tables._locks.Length; i < newLocks.Length; i++)
                    {
                        newLocks[i] = new object();
                    }
                }

                var newBuckets = new VolatileNode[newLength];
                var newCountPerLock = new int[newLocks.Length];
                var newTables = new Tables(newBuckets, newLocks, newCountPerLock);

                // Now acquire all other locks for the table
                AcquirePostFirstLock(tables, ref locksAcquired);

                // Copy all data into a new table, creating new nodes for all elements
                foreach (VolatileNode bucket in tables._buckets)
                {
                    Node? current = bucket._node;
                    while (current is not null)
                    {
                        int hashCode = current._hashcode;

                        Node? next = current._next;
                        ref Node? newBucket = ref GetBucketAndLock(newTables, hashCode, out uint newLockNo);

                        newBucket = new Node(current._key, current._value, hashCode, newBucket);

                        checked
                        {
                            newCountPerLock[newLockNo]++;
                        }

                        current = next;
                    }
                }

                // Adjust the budget
                _budget = Math.Max(1, newBuckets.Length / newLocks.Length);

                // Replace tables with the new versions
                _tables = newTables;
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        /// <summary>The number of concurrent writes for which to optimize by default.</summary>
        private static int DefaultConcurrencyLevel => Environment.ProcessorCount;

        /// <summary>
        /// Acquires all locks for this hash table, and increments locksAcquired by the number
        /// of locks that were successfully acquired. The locks are acquired in an increasing
        /// order.
        /// </summary>
        private void AcquireAllLocks(ref int locksAcquired)
        {
            // First, acquire lock 0, then acquire the rest. _tables won't change after acquiring lock 0.
            AcquireFirstLock(ref locksAcquired);
            AcquirePostFirstLock(_tables, ref locksAcquired);
            Debug.Assert(locksAcquired == _tables._locks.Length);
        }

        /// <summary>Acquires the first lock.</summary>
        /// <param name="locksAcquired">The number of locks acquired. It should be 0 on entry and 1 on exit.</param>
        /// <remarks>
        /// Once the caller owns the lock on lock 0, _tables._locks will not change (i.e., grow),
        /// so a caller can safely snap _tables._locks to read the remaining locks. When the locks array grows,
        /// even though the array object itself changes, the locks from the previous array are kept.
        /// </remarks>
        private void AcquireFirstLock(ref int locksAcquired)
        {
            object[] locks = _tables._locks;
            Debug.Assert(locksAcquired == 0);
            Debug.Assert(!Monitor.IsEntered(locks[0]));

            Monitor.Enter(locks[0]);
            locksAcquired = 1;
        }

        /// <summary>Acquires all of the locks after the first, which must already be acquired.</summary>
        /// <param name="tables">The tables snapped after the first lock was acquired.</param>
        /// <param name="locksAcquired">
        /// The number of locks acquired, which should be 1 on entry.  It's incremented as locks
        /// are taken so that the caller can reliably release those locks in a finally in case
        /// of exception.
        /// </param>
        private static void AcquirePostFirstLock(Tables tables, ref int locksAcquired)
        {
            object[] locks = tables._locks;
            Debug.Assert(Monitor.IsEntered(locks[0]));
            Debug.Assert(locksAcquired == 1);

            for (int i = 1; i < locks.Length; i++)
            {
                Monitor.Enter(locks[i]);
                locksAcquired++;
            }

            Debug.Assert(locksAcquired == locks.Length);
        }

        /// <summary>Releases all of the locks up to the specified number acquired.</summary>
        /// <param name="locksAcquired">The number of locks acquired.  All lock numbers in the range [0, locksAcquired) will be released.</param>
        private void ReleaseLocks(int locksAcquired)
        {
            Debug.Assert(locksAcquired >= 0);

            object[] locks = _tables._locks;
            for (int i = 0; i < locksAcquired; i++)
            {
                Monitor.Exit(locks[i]);
            }
        }

        /// <summary>
        /// Gets a collection containing the keys in the dictionary.
        /// </summary>
        private ReadOnlyCollection<string> Gestrings()
        {
            int locksAcquired = 0;
            try
            {
                AcquireAllLocks(ref locksAcquired);

                int count = GetCountNoLocks();
                if (count == 0)
                {
                    return ReadOnlyCollection<string>.Empty;
                }

                var keys = new string[count];
                int i = 0;
                foreach (VolatileNode bucket in _tables._buckets)
                {
                    for (Node? node = bucket._node; node is not null; node = node._next)
                    {
                        keys[i] = node._key;
                        i++;
                    }
                }
                Debug.Assert(i == count);

                return new ReadOnlyCollection<string>(keys);
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        /// <summary>
        /// Gets a collection containing the values in the dictionary.
        /// </summary>
        private ReadOnlyCollection<TValue> GetValues()
        {
            int locksAcquired = 0;
            try
            {
                AcquireAllLocks(ref locksAcquired);

                int count = GetCountNoLocks();
                if (count == 0)
                {
                    return ReadOnlyCollection<TValue>.Empty;
                }

                var keys = new TValue[count];
                int i = 0;
                foreach (VolatileNode bucket in _tables._buckets)
                {
                    for (Node? node = bucket._node; node is not null; node = node._next)
                    {
                        keys[i] = node._value;
                        i++;
                    }
                }
                Debug.Assert(i == count);

                return new ReadOnlyCollection<TValue>(keys);
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        private struct VolatileNode
        {
            // Workaround for https://github.com/dotnet/runtime/issues/65789.
            // If we had a Node?[] array, to safely read from the array we'd need to do Volatile.Read(ref array[i]),
            // but that triggers an unnecessary ldelema, which in turn results in a call to CastHelpers.LdelemaRef.
            // With this wrapper, the non-inlined call disappears.
            internal volatile Node? _node;
        }

        /// <summary>
        /// A node in a singly-linked list representing a particular hash table bucket.
        /// </summary>
        private sealed class Node
        {
            internal readonly string _key;
            internal TValue _value;
            internal volatile Node? _next;
            internal readonly int _hashcode;

            internal Node(string key, TValue value, int hashcode, Node? next)
            {
                _key = key;
                _value = value;
                _next = next;
                _hashcode = hashcode;
            }
        }

        /// <summary>Computes a ref to the bucket for a particular key.</summary>
        /// <remarks>This reads the bucket with a read acquire barrier.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Node? GetBucket(Tables tables, int hashcode)
        {
            VolatileNode[] buckets = tables._buckets;
            if (IntPtr.Size == 8)
            {
                return buckets[HashHelpers.FastMod((uint)hashcode, (uint)buckets.Length, tables._fastModBucketsMultiplier)]._node;
            }
            else
            {
                return buckets[(uint)hashcode % (uint)buckets.Length]._node;
            }
        }

        /// <summary>Computes the bucket and lock number for a particular key.</summary>
        /// <remarks>This returns a ref to the bucket node; no barriers are employed.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref Node? GetBucketAndLock(Tables tables, int hashcode, out uint lockNo)
        {
            VolatileNode[] buckets = tables._buckets;
            uint bucketNo;
            if (IntPtr.Size == 8)
            {
                bucketNo = HashHelpers.FastMod((uint)hashcode, (uint)buckets.Length, tables._fastModBucketsMultiplier);
            }
            else
            {
                bucketNo = (uint)hashcode % (uint)buckets.Length;
            }
            lockNo = bucketNo % (uint)tables._locks.Length; // doesn't use FastMod, as it would require maintaining a different multiplier
            return ref buckets[bucketNo]._node;
        }

        /// <summary>Tables that hold the internal state of the ConcurrentDictionary</summary>
        /// <remarks>Wrapping all of the mutable state into a single object allows us to swap in everything atomically.</remarks>
        private sealed class Tables
        {
            /// <summary>The comparer to use for lookups in the tables.</summary>
            /// <summary>A singly-linked list for each bucket.</summary>
            internal readonly VolatileNode[] _buckets;
            /// <summary>Pre-computed multiplier for use on 64-bit performing faster modulo operations.</summary>
            internal readonly ulong _fastModBucketsMultiplier;
            /// <summary>A set of locks, each guarding a section of the table.</summary>
            internal readonly object[] _locks;
            /// <summary>The number of elements guarded by each lock.</summary>
            internal readonly int[] _countPerLock;

            internal Tables(VolatileNode[] buckets, object[] locks, int[] countPerLock)
            {

                _buckets = buckets;
                _locks = locks;
                _countPerLock = countPerLock;
                if (IntPtr.Size == 8)
                {
                    _fastModBucketsMultiplier = HashHelpers.GetFastModMultiplier((uint)buckets.Length);
                }
            }
        }

        /// <summary>
        /// A private class to represent enumeration over the dictionary that implements the
        /// IDictionaryEnumerator interface.
        /// </summary>
        private sealed class DictionaryEnumerator : IDictionaryEnumerator
        {
            private readonly IEnumerator<KeyValuePair<string, TValue>> _enumerator; // Enumerator over the dictionary.

            internal DictionaryEnumerator(ConcurrentSpanDictionary<TValue> dictionary) => _enumerator = dictionary.GetEnumerator();

            public DictionaryEntry Entry => new DictionaryEntry(_enumerator.Current.Key, _enumerator.Current.Value);

            public object Key => _enumerator.Current.Key;

            public object? Value => _enumerator.Current.Value;

            public object Current => Entry;

            public bool MoveNext() => _enumerator.MoveNext();

            public void Reset() => _enumerator.Reset();
        }
    }

    internal static class ConcurrentDictionaryTypeProps<T>
    {
        /// <summary>Whether T's type can be written atomically (i.e., with no danger of torn reads).</summary>
        internal static readonly bool IsWriteAtomic = IsWriteAtomicPrivate();

        private static bool IsWriteAtomicPrivate()
        {
            // Section 12.6.6 of ECMA CLI explains which types can be read and written atomically without
            // the risk of tearing. See https://www.ecma-international.org/publications/files/ECMA-ST/ECMA-335.pdf

            if (!typeof(T).IsValueType ||
                typeof(T) == typeof(IntPtr) ||
                typeof(T) == typeof(UIntPtr))
            {
                return true;
            }

            switch (Type.GetTypeCode(typeof(T)))
            {
                case TypeCode.Boolean:
                case TypeCode.Byte:
                case TypeCode.Char:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.SByte:
                case TypeCode.Single:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                    return true;

                case TypeCode.Double:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                    return IntPtr.Size == 8;

                default:
                    return false;
            }
        }
    }


    file static class SpanCharHelper
    {
        public static unsafe bool SequenceEqual(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
        {
            if (first.Length != second.Length) return false;

            unsafe
            {
                fixed (char* f = first)          // 钉住数组
                fixed (char* s = second)          // 钉住数组
                {
                    ref char fc = ref *f;
                    ref char sc = ref *s;

                    return SequenceEqual(ref Unsafe.As<char, byte>(ref fc), ref Unsafe.As<char, byte>(ref sc), ((uint)first.Length) * (nuint)sizeof(char));
                }
            }
        }

        public static unsafe bool SequenceEqual(ref byte first, ref byte second, nuint length)
        {
            bool result;
            // Use nint for arithmetic to avoid unnecessary 64->32->64 truncations
            if (length >= (nuint)sizeof(nuint))
            {
                // Conditional jmp forward to favor shorter lengths. (See comment at "Equal:" label)
                // The longer lengths can make back the time due to branch misprediction
                // better than shorter lengths.
                goto Longer;
            }

#if TARGET_64BIT
            // On 32-bit, this will always be true since sizeof(nuint) == 4
            if (length < sizeof(uint))
#endif
            {
                uint differentBits = 0;
                nuint offset = (length & 2);
                if (offset != 0)
                {
                    differentBits = LoadUShort(ref first);
                    differentBits -= LoadUShort(ref second);
                }
                if ((length & 1) != 0)
                {
                    differentBits |= (uint)Unsafe.AddByteOffset(ref first, offset) - (uint)Unsafe.AddByteOffset(ref second, offset);
                }
                result = (differentBits == 0);
                goto Result;
            }
#if TARGET_64BIT
            else
            {
                nuint offset = length - sizeof(uint);
                uint differentBits = LoadUInt(ref first) - LoadUInt(ref second);
                differentBits |= LoadUInt(ref first, offset) - LoadUInt(ref second, offset);
                result = (differentBits == 0);
                goto Result;
            }
#endif
Longer:
// Only check that the ref is the same if buffers are large,
// and hence its worth avoiding doing unnecessary comparisons
            if (!Unsafe.AreSame(ref first, ref second))
            {
                // C# compiler inverts this test, making the outer goto the conditional jmp.
                goto Vector;
            }

            // This becomes a conditional jmp forward to not favor it.
            goto Equal;

Result:
            return result;
// When the sequence is equal; which is the longest execution, we want it to determine that
// as fast as possible so we do not want the early outs to be "predicted not taken" branches.
Equal:
            return true;

Vector:
            if (Vector128.IsHardwareAccelerated)
            {
                if (Vector512.IsHardwareAccelerated && length >= (nuint)Vector512<byte>.Count)
                {
                    nuint offset = 0;
                    nuint lengthToExamine = length - (nuint)Vector512<byte>.Count;
                    // Unsigned, so it shouldn't have overflowed larger than length (rather than negative)
                    Debug.Assert(lengthToExamine < length);
                    if (lengthToExamine != 0)
                    {
                        do
                        {
                            if (Vector512.LoadUnsafe(ref first, offset) !=
                                Vector512.LoadUnsafe(ref second, offset))
                            {
                                goto NotEqual;
                            }
                            offset += (nuint)Vector512<byte>.Count;
                        } while (lengthToExamine > offset);
                    }

                    // Do final compare as Vector512<byte>.Count from end rather than start
                    if (Vector512.LoadUnsafe(ref first, lengthToExamine) ==
                        Vector512.LoadUnsafe(ref second, lengthToExamine))
                    {
                        // C# compiler inverts this test, making the outer goto the conditional jmp.
                        goto Equal;
                    }

                    // This becomes a conditional jmp forward to not favor it.
                    goto NotEqual;
                }
                else if (Vector256.IsHardwareAccelerated && length >= (nuint)Vector256<byte>.Count)
                {
                    nuint offset = 0;
                    nuint lengthToExamine = length - (nuint)Vector256<byte>.Count;
                    // Unsigned, so it shouldn't have overflowed larger than length (rather than negative)
                    Debug.Assert(lengthToExamine < length);
                    if (lengthToExamine != 0)
                    {
                        do
                        {
                            if (Vector256.LoadUnsafe(ref first, offset) !=
                                Vector256.LoadUnsafe(ref second, offset))
                            {
                                goto NotEqual;
                            }
                            offset += (nuint)Vector256<byte>.Count;
                        } while (lengthToExamine > offset);
                    }

                    // Do final compare as Vector256<byte>.Count from end rather than start
                    if (Vector256.LoadUnsafe(ref first, lengthToExamine) ==
                        Vector256.LoadUnsafe(ref second, lengthToExamine))
                    {
                        // C# compiler inverts this test, making the outer goto the conditional jmp.
                        goto Equal;
                    }

                    // This becomes a conditional jmp forward to not favor it.
                    goto NotEqual;
                }
                else if (length >= (nuint)Vector128<byte>.Count)
                {
                    nuint offset = 0;
                    nuint lengthToExamine = length - (nuint)Vector128<byte>.Count;
                    // Unsigned, so it shouldn't have overflowed larger than length (rather than negative)
                    Debug.Assert(lengthToExamine < length);
                    if (lengthToExamine != 0)
                    {
                        do
                        {
                            if (Vector128.LoadUnsafe(ref first, offset) !=
                                Vector128.LoadUnsafe(ref second, offset))
                            {
                                goto NotEqual;
                            }
                            offset += (nuint)Vector128<byte>.Count;
                        } while (lengthToExamine > offset);
                    }

                    // Do final compare as Vector128<byte>.Count from end rather than start
                    if (Vector128.LoadUnsafe(ref first, lengthToExamine) ==
                        Vector128.LoadUnsafe(ref second, lengthToExamine))
                    {
                        // C# compiler inverts this test, making the outer goto the conditional jmp.
                        goto Equal;
                    }

                    // This becomes a conditional jmp forward to not favor it.
                    goto NotEqual;
                }
            }

#if TARGET_64BIT
            if (Vector128.IsHardwareAccelerated)
            {
                Debug.Assert(length <= (nuint)sizeof(nuint) * 2);

                nuint offset = length - (nuint)sizeof(nuint);
                nuint differentBits = LoadNUInt(ref first) - LoadNUInt(ref second);
                differentBits |= LoadNUInt(ref first, offset) - LoadNUInt(ref second, offset);
                result = (differentBits == 0);
                goto Result;
            }
            else
#endif
            {
                Debug.Assert(length >= (nuint)sizeof(nuint));
                {
                    nuint offset = 0;
                    nuint lengthToExamine = length - (nuint)sizeof(nuint);
                    // Unsigned, so it shouldn't have overflowed larger than length (rather than negative)
                    Debug.Assert(lengthToExamine < length);
                    if (lengthToExamine > 0)
                    {
                        do
                        {
                            // Compare unsigned so not do a sign extend mov on 64 bit
                            if (LoadNUInt(ref first, offset) != LoadNUInt(ref second, offset))
                            {
                                goto NotEqual;
                            }
                            offset += (nuint)sizeof(nuint);
                        } while (lengthToExamine > offset);
                    }

                    // Do final compare as sizeof(nuint) from end rather than start
                    result = (LoadNUInt(ref first, lengthToExamine) == LoadNUInt(ref second, lengthToExamine));
                    goto Result;
                }
            }

// As there are so many true/false exit points the Jit will coalesce them to one location.
// We want them at the end so the conditional early exit jmps are all jmp forwards so the
// branch predictor in a uninitialized state will not take them e.g.
// - loops are conditional jmps backwards and predicted
// - exceptions are conditional forwards jmps and not predicted
NotEqual:
            return false;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint LoadNUInt(ref byte start, nuint offset)
          => Unsafe.ReadUnaligned<nuint>(ref Unsafe.AddByteOffset(ref start, offset));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort LoadUShort(ref byte start)
              => Unsafe.ReadUnaligned<ushort>(ref start);


    }

}



