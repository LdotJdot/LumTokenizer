using LumTokenizer.RegexExpression;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LumTokenizer.Tokenizer
{

    /// <summary>
    /// Represents a tokenizer for converting text into a sequence of tokens.
    /// </summary>
    public sealed class ConcurrentBPETokenizer : IDisposable
    {
        const int initialSize = 128;
        private Regex _bpeParserRegex;

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
        private ConcurrentBPETokenizer(string[] bpeVocabLines, Dictionary<string, int> encoderJson, Dictionary<int, string> special, Regex regex)
        {
            _bpeParserRegex = regex;
            _encodings = encoderJson.ToFrozenDictionary();
            _vocabCount = encoderJson.Count;

            RegisterSpecialTokens(special, out _specialEnc, out _specialDecBytes);

            Initialize(bpeVocabLines, out _decodings, out _bpeRanks);

            splitter = new HighPerformanceSpanSplitter(special.Values);
        }

        public static ConcurrentBPETokenizer CreateTokenizer(string path, bool mergesAsString = false, RegexType regexType = RegexType.RegexCl100KBase, int vocabSize = 0)
        {
            var (bpeVocabLines, encoderJsonDictionary, special,regexStr) =
                mergesAsString
                ? TokMap.LoadFromTokenizerJson_MergesAsString(path)
                : TokMap.LoadFromTokenizerJson(path);

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

            Regex regex;

            if (regexType == RegexType.Custom)
            {
                if(string.IsNullOrWhiteSpace(regexStr))
                {
                    throw new ArgumentException("Custom regex string must be provided when regexType is Custom.");
                }
                regex = new Regex(regexStr, RegexOptions.Compiled);
            }
            else
            {
                regex = RegUtils.GetRegex(regexType);
            }

            return new ConcurrentBPETokenizer(bpeVocabLines, encoderJsonDictionary, special, regex);
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

            // 构建BPE合并排名
            var bpeMerges = bpeVocabLines.AsSpan(1, bpeVocabLines.Length - 2);
            for (int i = 0; i < bpeMerges.Length; i++)
            {
                var line = bpeMerges[i];
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    bpeRanks[ new (parts[0], parts[1])] = i;
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


 
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string[] GetBpeEntryForToken(ReadOnlySpan<char> token)
        {
            var word = new PooledList<string>(initialSize);
            var newWord = new PooledList<string>(initialSize);

            try
            {
          

                if (_cache.TryGetValue(token, out var cached))
                {
                    return cached;
                }

                if (token.Length == 0) return [];

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
                _cache[token] = val;
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
            var ssp = new PooledList<Range>(text.Length * 6);

            var bpeTokens = new List<int>(text.Length);

            if (handleSpecialToken)
            {

                splitter.Split(ssp,text);

                for (int i = 0; i < ssp.Count; i++)
                {
                    var subSp = ssp[i];

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
            var matches = _bpeParserRegex.EnumerateMatches(text);
            while (matches.MoveNext())
            {
                var match = matches.Current;
                var matchSpan = text.Slice(match.Index, match.Length);

                // 使用stackalloc避免堆分配
                byte[] utf8Bytes = _bytePool.Rent(encoding.GetMaxByteCount(match.Length));
                int bytesWritten = encoding.GetBytes(matchSpan, utf8Bytes);

                // 构建token字符串
                char[] tokenChars = _charPool.Rent(bytesWritten);

                for (int i = 0; i < bytesWritten; i++)
                {
                    tokenChars[i] = _byteEncoder[utf8Bytes[i]];
                }

                _bytePool.Return(utf8Bytes);

                // 获取BPE编码
                var bpeEntry = GetBpeEntryForToken(tokenChars.AsSpan(0, bytesWritten));

                _charPool.Return(tokenChars);

                foreach (var token in bpeEntry)
                {
                    if (_encodings.TryGetValue(token, out var tokenId))
                    {
                        output.Add(tokenId);
                    }
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