using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace LumTokenizer.Tokenizer
{

    readonly struct StringPair
    {
        public readonly string First;
        public readonly string Second;

        public StringPair(string value1, string value2)
        {
            First = value1;
            Second = value2;
        }


    }

    /// <summary>
    /// 单线程 BPE tokenizer。
    /// <para>
    /// <b>线程安全性：本类型不是线程安全的。</b>多线程并发使用同一实例的 <c>Encode</c> 会因为
    /// 内部 <c>_cache</c>（<see cref="SpanDictionary{TValue}"/>，非并发）以及 Encode 内部的
    /// 共享 PooledList 缓冲区导致数据竞争。需要并发场景请使用 <see cref="ConcurrentBPETokenizer"/>。
    /// </para>
    /// </summary>
    public sealed class BPETokenizer : IDisposable
    {
        const int initialSize = 128;
        private readonly TokenizerEncodePipeline _encodePipeline;

        private readonly int _vocabCount;
        private readonly FrozenDictionary<string, int> _encodings;
        private readonly FrozenDictionary<int, string> _decodings;
        private readonly char[] _byteEncoder = new char[256];
        private readonly byte[] _byteDecoder = new byte[1024]; // char to byte
        private readonly FrozenDictionary<StringPair, int> _bpeRanks; // 使用int而不是double
        private readonly SpanDictionary<string[]> _cache = new(initialSize);
        private readonly SpanDictionary<int> _specialEnc;
        private readonly FrozenDictionary<int, byte[]> _specialDecBytes;

        private readonly ArrayPool<char> _charPool = ArrayPool<char>.Shared;
        private readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Shared;

        HighPerformanceSpanSplitter splitter;
        public int VocabSize => _vocabCount;


        Encoding encoding = Encoding.UTF8;
        private BPETokenizer(string[] bpeVocabLines, Dictionary<string, int> encoderJson, Dictionary<int, string> special, NormalizerConfig? normalizer, PreTokenizerConfig? preTokenizer)
        {
            _encodings = encoderJson.ToFrozenDictionary();
            _vocabCount = encoderJson.Count;

            RegisterSpecialTokens(special, out _specialEnc, out _specialDecBytes);

            Initialize(bpeVocabLines, out _decodings, out _bpeRanks);

            _encodePipeline = TokenizerEncodePipeline.Compile(normalizer, preTokenizer, _byteEncoder, encoding);

            // 与 ConcurrentBPETokenizer 一致：按 special token 长度降序构造 splitter，保证最长前缀优先匹配。
            var orderedSpecials = new List<string>(special.Count);
            foreach (var v in special.Values)
                if (!string.IsNullOrEmpty(v))
                    orderedSpecials.Add(v);
            orderedSpecials.Sort(static (a, b) => b.Length.CompareTo(a.Length));
            splitter = new HighPerformanceSpanSplitter(orderedSpecials);
        }

        public static BPETokenizer CreateTokenizer(string path, int vocabSize = 0)
        {
            var (bpeVocabLines, encoderJsonDictionary, special, normalizer, preTokenizer) =
                TokMap.LoadFromTokenizerJson(path);

            if (vocabSize > 0 && encoderJsonDictionary.Count > vocabSize)
            {
                var keptTokenIds = encoderJsonDictionary
                    .OrderBy(kvp => kvp.Value)
                    .Take(vocabSize)
                    .Select(kvp => kvp.Value)
                    .ToHashSet();

                encoderJsonDictionary = encoderJsonDictionary
                    .Where(kvp => keptTokenIds.Contains(kvp.Value))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                bpeVocabLines = bpeVocabLines.Take(vocabSize).ToArray();
            }

            return new BPETokenizer(bpeVocabLines, encoderJsonDictionary, special, normalizer, preTokenizer);
        }

        private void RegisterSpecialTokens(Dictionary<int, string> special,
            out SpanDictionary<int> specialEnc,
            out FrozenDictionary<int, byte[]> specialDecBytes_froz
            )
        {
            specialEnc = new SpanDictionary<int>(special.Count);
            var specialDecBytes = new Dictionary<int, byte[]>(special.Count);

            foreach (var (id, text) in special)
            {
                specialEnc[text] = id;
                specialDecBytes[id] = encoding.GetBytes(text);
            }
            specialDecBytes_froz = specialDecBytes.ToFrozenDictionary();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Initialize(string[] bpeVocabLines, out FrozenDictionary<int, string> decoding_froz, out FrozenDictionary<StringPair, int> froneDict)
        {
            Dictionary<int, string> decodings = new(_vocabCount);

            var bpeRanks = new Dictionary<StringPair, int>();
            // 构建解码字典
            foreach (var (key, value) in _encodings)
            {
                decodings[value] = key;
            }

            // 构建 BPE 合并排名（与 ConcurrentBPETokenizer 同步修复）：
            // 之前 `AsSpan(1, Length - 2)` 会丢弃首尾 merge，且当 Length < 3 抛 ArgumentOutOfRangeException。
            // `TokMap.LoadFromTokenizerJson` 返回的 mergeLines 已是纯合并行（无版本注释/末尾占位），
            // 应当整段遍历。
            for (int i = 0; i < bpeVocabLines.Length; i++)
            {
                var line = bpeVocabLines[i];
                if (string.IsNullOrEmpty(line))
                    continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    bpeRanks[new(parts[0], parts[1])] = i;
                }
            }

            decoding_froz = decodings.ToFrozenDictionary();
            froneDict = bpeRanks.ToFrozenDictionary();
            // 初始化字节编码映射
            InitByteEncoderDecoder();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitByteEncoderDecoder()
        {
            // 初始化编码器
            for (int i = 0; i < 256; i++)
            {
                _byteEncoder[i] = (char)i;
            }

            // 特殊处理非ASCII字符
            int n = 0;
            var usedValues = new HashSet<int>();

            for (int b = 0; b < 256; b++)
            {
                if (b >= '!' && b <= '~')
                {
                    usedValues.Add(b);
                }
                else if (b >= 0xA1 && b <= 0xAC)
                {
                    usedValues.Add(b);
                }
                else if (b >= 0xAE && b <= 0xFF)
                {
                    usedValues.Add(b);
                }
            }

            for (int b = 0; b < 256; b++)
            {
                if (!usedValues.Contains(b))
                {
                    _byteEncoder[b] = (char)(256 + n);
                    n++;
                }
            }

            // 初始化解码器
            for (int i = 0; i < _byteEncoder.Length; i++)
            {
                _byteDecoder[_byteEncoder[i]] = (byte)i;
            }
        }

        List<string> word = new List<string>(initialSize);
        List<string> newWord = new List<string>(initialSize);


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool FindBestPair(ReadOnlySpan<string> word,
                         out StringPair bestPair,
                         out int bestRank)
        {
            bestPair = default;
            bestRank = int.MaxValue;
            bool found = false;

            if (word.Length < 2) return false;

            // 直接遍历寻找最佳合并对，避免创建中间集合
            for (int i = 0; i < word.Length - 1; i++)
            {
                StringPair currentPair = new(word[i], word[i + 1]);
                if (_bpeRanks.TryGetValue(currentPair, out int rank))
                {
                    if (rank < bestRank)
                    {
                        bestRank = rank;
                        bestPair = currentPair;
                        found = true;
                    }
                }
            }

            return found;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string[] GetBpeEntryForToken(ReadOnlySpan<char> token)
        {
            if (token.Length == 0) return [];

            if (_cache.TryGetValue(token, out var cached))
            {
                return cached;
            }

            word.Clear();
            foreach (var c in token)
            {
                word.Add(c.ToString());
            }

            while (true)
            {
                if (word.Count < 2) break;

                // 直接寻找最佳合并对
                if (!FindBestPair(CollectionsMarshal.AsSpan(word), out var bestPair, out var bestRank))
                {
                    break;
                }

                newWord.Clear();
                int i = 0;

                while (i < word.Count)
                {
                    // 查找匹配项
                    int j = -1;
                    for (int k = i; k < word.Count; k++)
                    {
                        if (word[k] == bestPair.First)
                        {
                            j = k;
                            break;
                        }
                    }

                    if (j == -1)
                    {
                        for (int k = i; k < word.Count; k++)
                        {
                            newWord.Add(word[k]);
                        }
                        break;
                    }

                    // 添加不匹配的部分
                    if (j > i)
                    {
                        for (int k = i; k < j; k++)
                        {
                            newWord.Add(word[k]);
                        }
                    }

                    i = j;

                    // 检查是否可以合并
                    if (i < word.Count - 1 && word[i] == bestPair.First && word[i + 1] == bestPair.Second)
                    {
                        newWord.Add($"{bestPair.First}{bestPair.Second}");
                        i += 2;
                    }
                    else
                    {
                        newWord.Add(word[i]);
                        i++;
                    }
                }

                word.Clear();
                (word, newWord) = (newWord, word);

                if (word.Count == 1) break;
            }

            var val = word.ToArray();
            // 与 ConcurrentBPETokenizer 同步加 soft cap，避免长运行时 _cache 单调增长 OOM。
            if (_cache.Count < SoftCacheLimit)
                _cache[token] = val;
            return val;
        }

        /// <summary>非并发版的内部 piece 缓存软上限；超过后停止写入（已有的仍可命中）。</summary>
        private const int SoftCacheLimit = 1_000_000;

        SpanStringCollection ssp = new SpanStringCollection();

        public List<int> Encode(string text, bool handleSpecialToken = true)
        {
            ArgumentNullException.ThrowIfNull(text);
            var bpeTokens = new List<int>(text.Length);
            if (text.Length == 0)
                return bpeTokens;

            if (handleSpecialToken)
            {

                ssp.SetOrigin(text);

                splitter.Split(ssp);

                for (int i = 0; i < ssp.Count; i++)
                {
                    var subSp = ssp[i];

                    if (_specialEnc.TryGetValue(subSp, out var id))
                    {
                        bpeTokens.Add(id);
                    }
                    else
                    {
                        ProcessRegularText(subSp, bpeTokens);
                    }
                }
            }
            else
            {
                ProcessRegularText(text, bpeTokens);
            }

            return bpeTokens;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessRegularText(ReadOnlySpan<char> text, List<int> output)
        {
            if (text.Length == 0)
                return;

            var chunk = text.ToString();
            var normalized = _encodePipeline.ApplyNormalizer(chunk);
            var pieces = new List<string>(32);
            _encodePipeline.PreTokenizeToPieces(normalized, pieces);

            foreach (var piece in pieces)
            {
                if (piece.Length == 0) continue;

                if (_encodePipeline.EndsWithByteLevelPreTokenizer)
                {
                    var bpeEntry = GetBpeEntryForToken(piece.AsSpan());
                    foreach (var token in bpeEntry)
                    {
                        if (_encodings.TryGetValue(token, out var tokenId))
                            output.Add(tokenId);
                    }
                    continue;
                }

                // try/finally 保证异常路径也释放 ArrayPool 借用，避免长运行慢性资源泄漏。
                byte[]? utf8Bytes = null;
                char[]? tokenChars = null;
                try
                {
                    utf8Bytes = _bytePool.Rent(encoding.GetMaxByteCount(piece.Length));
                    int bytesWritten = encoding.GetBytes(piece.AsSpan(), utf8Bytes);

                    tokenChars = _charPool.Rent(bytesWritten);

                    for (int bi = 0; bi < bytesWritten; bi++)
                        tokenChars[bi] = _byteEncoder[utf8Bytes[bi]];

                    var bpeEntry2 = GetBpeEntryForToken(tokenChars.AsSpan(0, bytesWritten));

                    foreach (var token in bpeEntry2)
                    {
                        if (_encodings.TryGetValue(token, out var tokenId))
                            output.Add(tokenId);
                    }
                }
                finally
                {
                    if (utf8Bytes is not null) _bytePool.Return(utf8Bytes);
                    if (tokenChars is not null) _charPool.Return(tokenChars);
                }
            }
        }


        public string Decode(IList<int> tokens, bool includeSpecial = true)
        {
            using var memoryStream = new MemoryStream();

            foreach (var tokenId in tokens)
            {
                if (includeSpecial && _specialDecBytes.TryGetValue(tokenId, out var special))
                {
                    memoryStream.Write(special, 0, special.Length);
                    continue;
                }

                if (!_decodings.TryGetValue(tokenId, out var token))
                {
                    continue;
                }

                // 直接写入字节
                foreach (var c in token)
                {
                    if (c < _byteDecoder.Length)
                    {
                        memoryStream.WriteByte(_byteDecoder[c]);
                    }
                }
            }

            // 转换为字符串
            return encoding.GetString(memoryStream.ToArray());

        }


        public void Dispose()
        {
            // 清理大缓存
            _cache.Clear();
            GC.SuppressFinalize(this);
        }
    }
}