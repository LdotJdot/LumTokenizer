using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LumTokenizer.Tokenizer;

/// <summary>
/// 从 tokenizer.json 的 <c>normalizer</c> 与 <c>pre_tokenizer</c> 编译出的编码前文本管线（与 HF tokenizers 语义对齐为主，极端配置可能略有差异）。
/// </summary>
public sealed class TokenizerEncodePipeline
{
    private readonly Func<string, string> _normalize;
    private readonly IPreTokenStep[] _preSteps;

    /// <summary>若 pre_tokenizer 链以 ByteLevel 结尾，则输出已是词表字符空间，后续不再做 UTF8+byte 映射。</summary>
    public bool EndsWithByteLevelPreTokenizer { get; }

    private TokenizerEncodePipeline(Func<string, string> normalize, IPreTokenStep[] preSteps, bool endsWithByteLevel)
    {
        _normalize = normalize;
        _preSteps = preSteps;
        EndsWithByteLevelPreTokenizer = endsWithByteLevel;
    }

    public static TokenizerEncodePipeline Compile(
        NormalizerConfig? normalizer,
        PreTokenizerConfig? preTokenizer,
        char[] byteEncoderTable,
        Encoding utf8)
    {
        var norm = CompileNormalizer(normalizer);
        var (steps, endsBl) = CompilePreTokenizer(preTokenizer, byteEncoderTable, utf8);
        return new TokenizerEncodePipeline(norm, steps, endsBl);
    }

    public string ApplyNormalizer(string text) => _normalize(text);

    public void PreTokenizeToPieces(string normalized, List<string> sink)
    {
        var cur = new List<string>(8) { normalized };
        var next = new List<string>(16);
        foreach (var step in _preSteps)
        {
            next.Clear();
            foreach (var p in cur)
            {
                if (p.Length == 0) continue;
                step.Apply(p, next);
            }
            (cur, next) = (next, cur);
        }

        sink.Clear();
        foreach (var s in cur)
        {
            if (s.Length > 0) sink.Add(s);
        }
    }

    private static Func<string, string> CompileNormalizer(NormalizerConfig? root)
    {
        if (root is null)
            return static s => s;

        var chain = new List<Func<string, string>>();

        if (root.Normalizers is { Count: > 0 })
            CollectNormalizerSteps(root.Normalizers, chain);
        else if (!string.IsNullOrEmpty(root.Type)
                 && !root.Type.Equals("Sequence", StringComparison.OrdinalIgnoreCase))
            CollectNormalizerSteps(new List<NormalizerItem> { new() { Type = root.Type } }, chain);

        if (chain.Count == 0) return static s => s;
        return s =>
        {
            var t = s;
            foreach (var f in chain) t = f(t);
            return t;
        };
    }

    private static void CollectNormalizerSteps(List<NormalizerItem> items, List<Func<string, string>> chain)
    {
        foreach (var it in items)
        {
            var ty = it.Type ?? string.Empty;
            if (ty.Equals("Sequence", StringComparison.OrdinalIgnoreCase))
            {
                if (it.Normalizers is { Count: > 0 })
                    CollectNormalizerSteps(it.Normalizers, chain);
                continue;
            }

            if (ty.Equals("Lowercase", StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(s => s.ToLowerInvariant());
                continue;
            }

            if (ty.Equals("NFC", StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(s => s.Normalize(NormalizationForm.FormC));
                continue;
            }

            if (ty.Equals("NFD", StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(s => s.Normalize(NormalizationForm.FormD));
                continue;
            }

            if (ty.Equals("NFKC", StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(s => s.Normalize(NormalizationForm.FormKC));
                continue;
            }

            if (ty.Equals("NFKD", StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(s => s.Normalize(NormalizationForm.FormKD));
                continue;
            }

            if (ty.Equals("Strip", StringComparison.OrdinalIgnoreCase))
            {
                var left = it.Left ?? true;
                var right = it.Right ?? true;
                chain.Add(s =>
                {
                    if (left && right) return s.Trim();
                    if (left) return s.TrimStart();
                    if (right) return s.TrimEnd();
                    return s;
                });
                continue;
            }

            if (ty.Equals("Replace", StringComparison.OrdinalIgnoreCase))
            {
                var pat = it.PatternExpression;
                var rep = it.Content ?? string.Empty;
                if (string.IsNullOrEmpty(pat))
                    throw new InvalidOperationException("normalizer Replace 缺少 pattern。");
                var rx = new Regex(pat, RegexOptions.Compiled | RegexOptions.CultureInvariant);
                chain.Add(s => rx.Replace(s, rep));
                continue;
            }

            if (ty.Equals("StripAccents", StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(StripAccentsImpl);
                continue;
            }

            if (ty.Equals("ByteLevel", StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(s => ByteLevelNormalizeString(s, utf8: Encoding.UTF8)); // normalizer ByteLevel: map bytes of UTF8
                continue;
            }

            if (ty.Equals("Nmt", StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(NmtNormalize);
                continue;
            }

            if (ty.Equals("BertNormalizer", StringComparison.OrdinalIgnoreCase))
            {
                var clean = it.CleanText ?? true;
                var han = it.HandleChineseChars ?? true;
                var stripAcc = it.StripAccents ?? (it.Lowercase ?? true);
                var lower = it.Lowercase ?? true;
                chain.Add(s => BertNormalize(s, clean, han, stripAcc, lower));
                continue;
            }

            if (ty.Equals("Precompiled", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("normalizer 类型 Precompiled 需要 SentencePiece 二进制映射，当前未实现。");

            throw new NotSupportedException($"不支持的 normalizer 类型：{ty}");
        }
    }

    private static string StripAccentsImpl(string s)
    {
        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string NmtNormalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '\n' or '\r' or '\t') { sb.Append(ch); continue; }
            if (char.IsControl(ch)) { sb.Append(' '); continue; }
            sb.Append(ch);
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ", RegexOptions.Compiled).Trim();
    }

    private static string BertNormalize(string s, bool cleanText, bool handleChinese, bool stripAccents, bool lowercase)
    {
        var t = s;
        if (cleanText)
        {
            var sb = new StringBuilder(t.Length);
            foreach (var ch in t)
            {
                if (ch == 0 || ch == char.MaxValue) continue;
                if (char.IsControl(ch) && ch != '\t' && ch != '\n' && ch != '\r') { sb.Append(' '); continue; }
                sb.Append(ch == '\r' || ch == '\n' ? ' ' : ch);
            }
            t = Regex.Replace(sb.ToString(), @"\s+", " ", RegexOptions.Compiled).Trim();
        }

        if (handleChinese)
            t = Regex.Replace(t, @"([\u4e00-\u9fff])(?=[\u4e00-\u9fff])", "$1 ", RegexOptions.Compiled);

        if (stripAccents)
            t = StripAccentsImpl(t);

        if (lowercase)
            t = t.ToLowerInvariant();

        return t;
    }

    private static string ByteLevelNormalizeString(string s, Encoding utf8)
    {
        Span<char> table = stackalloc char[256];
        InitLocalByteTable(table);
        var bytes = utf8.GetBytes(s);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
            sb.Append(table[b]);
        return sb.ToString();
    }

    private static void InitLocalByteTable(Span<char> table)
    {
        for (var i = 0; i < 256; i++) table[i] = (char)i;
        var used = new bool[256];
        for (var b = 0; b < 256; b++)
        {
            if (b is >= '!' and <= '~') used[b] = true;
            else if (b is >= 0xA1 and <= 0xAC) used[b] = true;
            else if (b is >= 0xAE and <= 0xFF) used[b] = true;
        }
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            if (!used[b])
            {
                table[b] = (char)(256 + n);
                n++;
            }
        }
    }

    private static (IPreTokenStep[] steps, bool endsWithByteLevel) CompilePreTokenizer(
        PreTokenizerConfig? root,
        char[] byteEncoderTable,
        Encoding utf8)
    {
        var list = new List<IPreTokenStep>();
        if (root?.Pretokenizers is { Count: > 0 })
            CollectPreSteps(root.Pretokenizers, list, byteEncoderTable, utf8);
        var arr = list.ToArray();
        var endsBl = arr.Length > 0 && arr[^1] is ByteLevelPreTokenStep;
        return (arr, endsBl);
    }

    private static void CollectPreSteps(
        List<PreTokenizerItem> items,
        List<IPreTokenStep> sink,
        char[] byteEncoderTable,
        Encoding utf8)
    {
        foreach (var it in items)
        {
            var ty = it.Type ?? string.Empty;
            if (ty.Equals("Sequence", StringComparison.OrdinalIgnoreCase))
            {
                if (it.Pretokenizers is { Count: > 0 })
                    CollectPreSteps(it.Pretokenizers, sink, byteEncoderTable, utf8);
                continue;
            }

            if (ty.Equals("Split", StringComparison.OrdinalIgnoreCase))
            {
                var pat = it.PatternExpression;
                if (string.IsNullOrWhiteSpace(pat))
                    throw new InvalidOperationException("pre_tokenizer Split 缺少 pattern。");
                var rx = new Regex(pat, RegexOptions.Compiled | RegexOptions.CultureInvariant);
                var behavior = it.Behavior ?? "Isolated";
                sink.Add(new SplitPreTokenStep(rx, behavior, it.Invert ?? false));
                continue;
            }

            if (ty.Equals("ByteLevel", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(new ByteLevelPreTokenStep(
                    it.AddPrefixSpace ?? true,
                    it.UseRegex ?? true,
                    byteEncoderTable,
                    utf8));
                continue;
            }

            if (ty.Equals("Whitespace", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(WhitespaceStep.Instance);
                continue;
            }

            if (ty.Equals("WhitespaceSplit", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(WhitespaceSplitStep.Instance);
                continue;
            }

            if (ty.Equals("BertPreTokenizer", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(BertPreTokenizerStep.Instance);
                continue;
            }

            if (ty.Equals("Digits", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(new DigitsPreTokenStep(it.IndividualDigits ?? false));
                continue;
            }

            if (ty.Equals("Punctuation", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(new PunctuationPreTokenStep(it.Behavior ?? "Isolated"));
                continue;
            }

            if (ty.Equals("Metaspace", StringComparison.OrdinalIgnoreCase))
            {
                var rep = string.IsNullOrEmpty(it.Replacement) ? "\u2581" : it.Replacement!;
                if (rep.Length != 1)
                    throw new InvalidOperationException("Metaspace replacement 必须为单个字符。");
                sink.Add(new MetaspacePreTokenStep(rep[0], it.PrependScheme ?? "always"));
                continue;
            }

            if (ty.Equals("CharDelimiterSplit", StringComparison.OrdinalIgnoreCase))
            {
                var d = it.Delimiter;
                if (string.IsNullOrEmpty(d) || d.Length != 1)
                    throw new InvalidOperationException("CharDelimiterSplit 需要单个字符 delimiter。");
                sink.Add(new CharDelimiterSplitStep(d[0]));
                continue;
            }

            if (ty.Equals("Replace", StringComparison.OrdinalIgnoreCase))
            {
                var pat = it.PatternExpression;
                if (string.IsNullOrEmpty(pat))
                    throw new InvalidOperationException("pre_tokenizer Replace 缺少 pattern。");
                var rx = new Regex(pat, RegexOptions.Compiled | RegexOptions.CultureInvariant);
                sink.Add(new ReplacePreTokenStep(rx, it.ReplaceContent ?? string.Empty));
                continue;
            }

            if (ty.Equals("Strip", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(new StripPreTokenStep(it.Lstrip ?? true, it.Rstrip ?? true));
                continue;
            }

            if (ty.Equals("UnicodeScripts", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(UnicodeScriptsStep.Instance);
                continue;
            }

            throw new NotSupportedException($"不支持的 pre_tokenizer 类型：{ty}");
        }
    }

    private interface IPreTokenStep
    {
        void Apply(string input, List<string> output);
    }

    private sealed class SplitPreTokenStep : IPreTokenStep
    {
        private readonly Regex _rx;
        private readonly string _behavior;
        private readonly bool _invert;

        public SplitPreTokenStep(Regex rx, string behavior, bool invert)
        {
            _rx = rx;
            _behavior = behavior.ToLowerInvariant();
            _invert = invert;
        }

        public void Apply(string input, List<string> output)
        {
            foreach (var p in SplitCore(input, _rx, _behavior, _invert))
                if (p.Length > 0) output.Add(p);
        }

        internal static IEnumerable<string> SplitCore(string s, Regex rx, string behavior, bool invert)
        {
            var b = behavior.Replace("_", "").Replace("-", "");
            // isolated, removed, mergedwithprevious, mergedwithnext, contiguous

            var spans = new List<(bool isMatch, int start, int len)>();
            var e = rx.EnumerateMatches(s);
            var idx = 0;
            while (e.MoveNext())
            {
                var m = e.Current;
                if (m.Index > idx)
                    spans.Add((false, idx, m.Index - idx));
                spans.Add((true, m.Index, m.Length));
                idx = m.Index + m.Length;
            }
            if (idx < s.Length)
                spans.Add((false, idx, s.Length - idx));

            if (b == "contiguous")
                CollapseContiguousMatches(spans);

            if (invert)
            {
                foreach (var (isM, st, ln) in spans)
                {
                    if (!isM && ln > 0) yield return s.Substring(st, ln);
                }
                yield break;
            }

            if (b == "removed")
            {
                foreach (var (isM, st, ln) in spans)
                    if (!isM && ln > 0) yield return s.Substring(st, ln);
                yield break;
            }

            if (b == "mergedwithprevious")
            {
                for (var i = 0; i < spans.Count; i++)
                {
                    var (isM, st, ln) = spans[i];
                    if (ln == 0) continue;
                    if (!isM && i + 1 < spans.Count && spans[i + 1].isMatch)
                    {
                        var m = spans[i + 1];
                        var end = m.start + m.len;
                        yield return s.Substring(st, end - st);
                        i++;
                    }
                    else if (!isM)
                        yield return s.Substring(st, ln);
                    else
                        yield return s.Substring(st, ln);
                }
                yield break;
            }

            if (b == "mergedwithnext")
            {
                string? pendingMatch = null;
                foreach (var (isM, st, ln) in spans)
                {
                    if (ln == 0) continue;
                    var piece = s.Substring(st, ln);
                    if (isM) pendingMatch = piece;
                    else
                    {
                        if (pendingMatch != null) { yield return pendingMatch + piece; pendingMatch = null; }
                        else yield return piece;
                    }
                }
                if (pendingMatch != null) yield return pendingMatch;
                yield break;
            }

            // isolated (default)
            foreach (var (isM, st, ln) in spans)
                if (ln > 0) yield return s.Substring(st, ln);
        }

        private static void CollapseContiguousMatches(List<(bool isMatch, int start, int len)> spans)
        {
            for (var i = 0; i < spans.Count - 1;)
            {
                if (spans[i].isMatch && spans[i + 1].isMatch)
                {
                    var mergedLen = spans[i].len + spans[i + 1].len;
                    spans[i] = (true, spans[i].start, mergedLen);
                    spans.RemoveAt(i + 1);
                }
                else i++;
            }
        }
    }

    private sealed class ByteLevelPreTokenStep : IPreTokenStep
    {
        private static readonly Regex GptSplit = new(
            @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly bool _addPrefixSpace;
        private readonly bool _useRegex;
        private readonly char[] _byteTable;
        private readonly Encoding _utf8;

        public ByteLevelPreTokenStep(bool addPrefixSpace, bool useRegex, char[] byteTable, Encoding utf8)
        {
            _addPrefixSpace = addPrefixSpace;
            _useRegex = useRegex;
            _byteTable = byteTable;
            _utf8 = utf8;
        }

        public void Apply(string input, List<string> output)
        {
            var spaceChar = _byteTable[' '];
            if (!_useRegex)
            {
                output.Add(EncodePart(input.AsSpan()));
                return;
            }

            var first = true;
            foreach (var m in GptSplit.EnumerateMatches(input))
            {
                if (m.Length == 0) continue;
                var seg = input.AsSpan(m.Index, m.Length);
                var enc = EncodePart(seg);
                if (_addPrefixSpace && !first) enc = spaceChar + enc;
                else if (_addPrefixSpace && first && seg.Length > 0 && seg[0] != ' ')
                    enc = spaceChar + enc;
                first = false;
                output.Add(enc);
            }
            if (first && input.Length > 0)
                output.Add(EncodePart(input.AsSpan()));
        }

        private string EncodePart(ReadOnlySpan<char> seg)
        {
            Span<byte> buf = stackalloc byte[_utf8.GetMaxByteCount(seg.Length)];
            var n = _utf8.GetBytes(seg, buf);
            var sb = new StringBuilder(n);
            for (var i = 0; i < n; i++)
                sb.Append(_byteTable[buf[i]]);
            return sb.ToString();
        }
    }

    private sealed class WhitespaceStep : IPreTokenStep
    {
        internal static readonly WhitespaceStep Instance = new();
        private static readonly Regex Rx = new(@"\w+|[^\w\s]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public void Apply(string input, List<string> output)
        {
            foreach (var m in Rx.EnumerateMatches(input))
            {
                if (m.Length == 0) continue;
                output.Add(input.Substring(m.Index, m.Length));
            }
        }
    }

    private sealed class WhitespaceSplitStep : IPreTokenStep
    {
        internal static readonly WhitespaceSplitStep Instance = new();

        public void Apply(string input, List<string> output)
        {
            foreach (var w in Regex.Split(input, @"\s+", RegexOptions.Compiled))
                if (w.Length > 0) output.Add(w);
        }
    }

    private sealed class BertPreTokenizerStep : IPreTokenStep
    {
        internal static readonly BertPreTokenizerStep Instance = new();

        public void Apply(string input, List<string> output)
        {
            foreach (var w in Regex.Split(input, @"\s+", RegexOptions.Compiled))
            {
                if (w.Length == 0) continue;
                SplitWord(w, output);
            }
        }

        private static void SplitWord(string word, List<string> output)
        {
            var sb = new StringBuilder();
            foreach (var ch in word)
            {
                if (IsPunct(ch))
                {
                    if (sb.Length > 0) { output.Add(sb.ToString()); sb.Clear(); }
                    output.Add(ch.ToString());
                }
                else sb.Append(ch);
            }
            if (sb.Length > 0) output.Add(sb.ToString());
        }

        private static bool IsPunct(char c)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation;
        }
    }

    private sealed class DigitsPreTokenStep : IPreTokenStep
    {
        private readonly bool _individual;

        public DigitsPreTokenStep(bool individual) => _individual = individual;

        public void Apply(string input, List<string> output)
        {
            var sb = new StringBuilder();
            bool? inDigit = null;
            void Flush()
            {
                if (sb.Length == 0) return;
                var chunk = sb.ToString();
                sb.Clear();
                if (_individual && chunk.All(char.IsDigit))
                {
                    foreach (var c in chunk) output.Add(c.ToString());
                }
                else output.Add(chunk);
            }

            foreach (var ch in input)
            {
                var isD = char.IsDigit(ch);
                if (inDigit == null) inDigit = isD;
                else if (inDigit.Value != isD) { Flush(); inDigit = isD; }
                sb.Append(ch);
            }
            Flush();
        }
    }

    private sealed class PunctuationPreTokenStep : IPreTokenStep
    {
        private readonly string _behavior;

        public PunctuationPreTokenStep(string behavior) => _behavior = behavior.ToLowerInvariant().Replace("_", "");

        public void Apply(string input, List<string> output)
        {
            var rx = new Regex(@"\w+|\p{P}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            var beh = _behavior == "contiguous" ? "contiguous" : _behavior;
            foreach (var piece in SplitPreTokenStep.SplitCore(input, rx, beh, false))
                if (piece.Length > 0) output.Add(piece);
        }
    }

    private sealed class MetaspacePreTokenStep : IPreTokenStep
    {
        private readonly char _rep;
        private readonly string _scheme;

        public MetaspacePreTokenStep(char rep, string scheme)
        {
            _rep = rep;
            _scheme = scheme.ToLowerInvariant();
        }

        public void Apply(string input, List<string> output)
        {
            var first = true;
            foreach (var w in Regex.Split(input, @"\s+", RegexOptions.Compiled))
            {
                if (w.Length == 0) continue;
                var prefix = _scheme switch
                {
                    "never" => "",
                    "first" => first ? _rep.ToString() : string.Empty,
                    _ => _rep.ToString()
                };
                first = false;
                output.Add(prefix + w);
            }
        }
    }

    private sealed class CharDelimiterSplitStep : IPreTokenStep
    {
        private readonly char _d;

        public CharDelimiterSplitStep(char d) => _d = d;

        public void Apply(string input, List<string> output)
        {
            foreach (var p in input.Split(_d, StringSplitOptions.RemoveEmptyEntries))
                if (p.Length > 0) output.Add(p);
        }
    }

    private sealed class ReplacePreTokenStep : IPreTokenStep
    {
        private readonly Regex _rx;
        private readonly string _content;

        public ReplacePreTokenStep(Regex rx, string content)
        {
            _rx = rx;
            _content = content;
        }

        public void Apply(string input, List<string> output) =>
            output.Add(_rx.Replace(input, _content));
    }

    private sealed class StripPreTokenStep : IPreTokenStep
    {
        private readonly bool _left;
        private readonly bool _right;

        public StripPreTokenStep(bool left, bool right)
        {
            _left = left;
            _right = right;
        }

        public void Apply(string input, List<string> output)
        {
            var s = input;
            if (_left && _right) s = s.Trim();
            else if (_left) s = s.TrimStart();
            else if (_right) s = s.TrimEnd();
            if (s.Length > 0) output.Add(s);
        }
    }

    private sealed class UnicodeScriptsStep : IPreTokenStep
    {
        internal static readonly UnicodeScriptsStep Instance = new();

        public void Apply(string input, List<string> output)
        {
            var sb = new StringBuilder();
            int? bucket = null;
            foreach (var rune in input.EnumerateRunes())
            {
                var b = ScriptBucket(rune.Value);
                if (bucket == null) bucket = b;
                else if (bucket != b)
                {
                    if (sb.Length > 0) output.Add(sb.ToString());
                    sb.Clear();
                    bucket = b;
                }
                sb.Append(rune.ToString());
            }
            if (sb.Length > 0) output.Add(sb.ToString());
        }

        private static int ScriptBucket(int cp)
        {
            if (cp is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x3040 and <= 0x30FF)
                return 1;
            if (cp is >= 0xAC00 and <= 0xD7AF) return 2;
            if (cp is >= 0x0400 and <= 0x04FF) return 3;
            if (cp is >= 0x0600 and <= 0x06FF) return 4;
            if (cp is >= 0x0900 and <= 0x097F) return 5;
            if ((cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z') || (cp >= 0x00C0 && cp <= 0x024F))
                return 0;
            return 6;
        }
    }
}
