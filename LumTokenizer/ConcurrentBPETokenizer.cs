using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace LumTokenizer.Tokenizer
{

    /// <summary>
    /// Represents a tokenizer for converting text into a sequence of tokens.
    /// </summary>
    public sealed class ConcurrentBPETokenizer : IDisposable
    {
        public int MaxCache { get; } = 100_0000;
        const int initialSize = 128;
        private readonly TokenizerEncodePipeline _encodePipeline;

        private readonly int _vocabCount;
        private readonly FrozenDictionary<string, int> _encodings;
        private readonly FrozenDictionary<int, string> _decodings;
        private readonly char[] _byteEncoder = new char[256];
        private readonly byte[] _byteDecoder = new byte[1024]; // char to byte
        private readonly FrozenDictionary<StringPair, int> _bpeRanks; // 使用int而不是double
        private readonly ConcurrentSpanDictionary<string[]> _cache = new(initialSize);
        private readonly SpanDictionary<int> _specialEnc;
        private readonly FrozenDictionary<int, byte[]> _specialDecBytes;

        private readonly ArrayPool<char> _charPool = ArrayPool<char>.Shared;
        private readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Shared;

        HighPerformanceSpanSplitter splitter;
        public int VocabSize => _vocabCount;


        Encoding encoding = Encoding.UTF8;
        private ConcurrentBPETokenizer(string[] bpeVocabLines, Dictionary<string, int> encoderJson, Dictionary<int, string> special, NormalizerConfig? normalizer, PreTokenizerConfig? preTokenizer)
        {
            _encodings = encoderJson.ToFrozenDictionary();
            _vocabCount = encoderJson.Count;

            RegisterSpecialTokens(special, out _specialEnc, out _specialDecBytes);

            Initialize(bpeVocabLines, out _decodings, out _bpeRanks);

            _encodePipeline = TokenizerEncodePipeline.Compile(normalizer, preTokenizer, _byteEncoder, encoding);

            splitter = new HighPerformanceSpanSplitter(OrderSpecialTokensByLengthDesc(special));
        }

        public static ConcurrentBPETokenizer CreateTokenizer(string path, int vocabSize = 0)
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

            return new ConcurrentBPETokenizer(bpeVocabLines, encoderJsonDictionary, special, normalizer, preTokenizer);
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
                if (string.IsNullOrEmpty(text))
                    continue;
                specialEnc[text] = id;
                specialDecBytes[id] = encoding.GetBytes(text);
            }
            specialDecBytes_froz = specialDecBytes.ToFrozenDictionary();
        }

        /// <summary>
        /// 构造 splitter 时需要按「长度降序」喂入 special token 字符串，避免短前缀 token 抢占长 token；
        /// 与上层调用方原先传入 Dictionary.Values 的「不稳定枚举顺序」相比，本方法的顺序是确定且最优的。
        /// </summary>
        private static List<string> OrderSpecialTokensByLengthDesc(Dictionary<int, string> special)
        {
            var list = new List<string>(special.Count);
            foreach (var v in special.Values)
                if (!string.IsNullOrEmpty(v))
                    list.Add(v);
            list.Sort(static (a, b) => b.Length.CompareTo(a.Length));
            return list;
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

            // 构建 BPE 合并排名。
            // 关键修复：`TokMap.LoadFromTokenizerJson` 返回的 mergeLines 不包含 GPT-2 `merges.txt` 那种
            // 「首行版本注释 + 末行空白占位」结构，整块都是真正的 merge 行。
            // 之前用 `AsSpan(1, Length - 2)` 会：
            //   - 在长度 < 3 时抛 ArgumentOutOfRangeException；
            //   - 在长度 >= 3 时丢掉第 0 行与最后一行 merge，直接导致 BPE 排名与 HuggingFace 不一致，
            //     进而影响所有依赖该 tokenizer 的 token 计数（含计费场景）。
            // 修复为「使用全量 mergeLines，对每行做防御性校验」。
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

        //long hit=0;
        //long total=0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string[] GetBpeEntryForToken(ReadOnlySpan<char> token)
        {
            if (token.Length == 0) return [];
            var word = new PooledList<string>(initialSize);
            var newWord = new PooledList<string>(initialSize);

            //Interlocked.Increment(ref total);

            try
            {

                if (MaxCache > 0 && _cache.TryGetValue(token, out var cached))
                {
                    // Console.WriteLine($"_cache hit: {Interlocked.Increment(ref hit)/(double)total*100} Count {_cache.Count}");
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
                    if (!FindBestPair(word.AsSpan(), out var bestPair, out var bestRank))
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

                // 关键修复：之前的「Count <= MaxCache 后再写入」是非原子的两步操作，多线程并发时
                // 条目数会无界增长。这里改用「先尝试 TryAdd；溢出则不写入」的乐观策略——这是 soft cap，
                // 极端场景下可能短暂略超 MaxCache，但不会持续增长（写不进就不再增长，已写入条目仍可命中）。
                // 真正的硬上限需要内部双链表 + 锁，权衡后这里取「内存安全 + 简洁」。
                if (MaxCache > 0 && _cache.Count < MaxCache)
                {
                    _cache.TryAdd(token, val);
                }

                return val;
            }
            finally
            {
                word.Dispose();
                newWord.Dispose();
            }
        }


        public List<int> Encode(string text, bool handleSpecialToken = true)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Length == 0)
                return new List<int>(0);

            // 关键修复：之前 `var ssp = new PooledList<Range>(...)` 从未 Dispose，
            // 导致每次 Encode 都租用 ArrayPool 数组但永不归还，长运行场景下 ArrayPool
            // 退化为持续新分配 + GC 压力上升的内存泄漏。改为 using。
            // 同时对极端长度做防御性 clamp，避免 `text.Length * 6` 算术溢出。
            int hintCap = text.Length switch
            {
                <= 0 => 16,
                > 0x0AAA_AAAA => int.MaxValue / 8, // 防溢出 clamp
                _ => text.Length * 6
            };
            using var ssp = new PooledList<Range>(hintCap);

            var bpeTokens = new List<int>(text.Length);

            if (handleSpecialToken)
            {

                splitter.Split(ssp, text);

                for (int i = 0; i < ssp.Count; i++)
                {
                    if (_specialEnc.TryGetValue(text.AsSpan(ssp[i]), out var id))
                    {
                        bpeTokens.Add(id);
                    }
                    else
                    {
                        ProcessRegularText(text.AsSpan(ssp[i]), bpeTokens);
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
                    EncodeFromByteLevelMapped(piece.AsSpan(), output);
                    continue;
                }

                // 关键修复：原实现把两块 ArrayPool 借用都放在「直线代码」上，任何中间异常都会漏 Return，
                // 长运行下会让 ArrayPool 退化成持续新分配；下面改成 try/finally 双保险。
                byte[]? utf8Bytes = null;
                char[]? tokenChars = null;
                try
                {
                    utf8Bytes = _bytePool.Rent(encoding.GetMaxByteCount(piece.Length));
                    int bytesWritten = encoding.GetBytes(piece.AsSpan(), utf8Bytes);

                    tokenChars = _charPool.Rent(bytesWritten);

                    for (int bi = 0; bi < bytesWritten; bi++)
                        tokenChars[bi] = _byteEncoder[utf8Bytes[bi]];

                    var bpeEntry = GetBpeEntryForToken(tokenChars.AsSpan(0, bytesWritten));

                    foreach (var token in bpeEntry)
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

        private void EncodeFromByteLevelMapped(ReadOnlySpan<char> piece, List<int> output)
        {
            var bpeEntry = GetBpeEntryForToken(piece);
            foreach (var token in bpeEntry)
            {
                if (_encodings.TryGetValue(token, out var tokenId))
                    output.Add(tokenId);
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