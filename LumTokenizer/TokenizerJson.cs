using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LumTokenizer.Tokenizer;

public class TokenizerJson
{
    [JsonPropertyName("model")]
    public Model Model { get; set; }

    [JsonPropertyName("normalizer")]
    public NormalizerConfig? Normalizer { get; set; }
    
    [JsonPropertyName("pre_tokenizer")]
    public PreTokenizerConfig? PreTokenizerConfig { get; set; }

    [JsonPropertyName("added_tokens")]
    public List<AddedToken> AddedTokens { get; set; }

    public Dictionary<int, string> SpecialTokens() =>
       AddedTokens.ToDictionary(at => at.Id, at => at.Content);
}
public class AddedToken
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; }
}

public class Model
{
    [JsonPropertyName("vocab")]
    public Dictionary<string, int> Vocab { get; set; }

    [JsonPropertyName("merges")]          // ←补上
    public List<List<string>> Merges { get; set; }
}

/// <summary>
/// 反序列化 HF <c>Split.pattern</c>：可为 JSON 字符串、<c>{"Regex":"..."}</c>、<c>{"String":"..."}</c>（字面量按正则转义）等。
/// </summary>
public sealed class SplitPatternJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.StartObject => ReadPatternObject(ref reader),
            _ => throw new JsonException($"pre_tokenizer Split.pattern 不支持的 JSON 形态：{reader.TokenType}")
        };
    }

    private static string? ReadPatternObject(ref Utf8JsonReader reader)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("Regex") || prop.NameEquals("regex"))
                return prop.Value.GetString();
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("String") || prop.NameEquals("string"))
            {
                var s = prop.Value.GetString();
                return string.IsNullOrEmpty(s) ? s : Regex.Escape(s);
            }
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>
/// 对应 HuggingFace tokenizer.json 的 pre_tokenizer 节点
/// </summary>
public class PreTokenizerConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Sequence";

    [JsonPropertyName("pretokenizers")]
    public List<PreTokenizerItem> Pretokenizers { get; set; } = new();
}

public class PreTokenizerItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>嵌套 <c>Sequence</c> 时的子 pretokenizers。</summary>
    [JsonPropertyName("pretokenizers")]
    public List<PreTokenizerItem>? Pretokenizers { get; set; }

    // ---------- Split 专用 ----------
    [JsonPropertyName("pattern")]
    [JsonConverter(typeof(SplitPatternJsonConverter))]
    public string? PatternExpression { get; set; }

    [JsonPropertyName("behavior")]
    public string? Behavior { get; set; }

    [JsonPropertyName("invert")]
    public bool? Invert { get; set; }

    // ---------- ByteLevel 专用 ----------
    [JsonPropertyName("add_prefix_space")]
    public bool? AddPrefixSpace { get; set; }

    [JsonPropertyName("trim_offsets")]
    public bool? TrimOffsets { get; set; }

    [JsonPropertyName("use_regex")]
    public bool? UseRegex { get; set; }

    // ---------- CharDelimiterSplit ----------
    [JsonPropertyName("delimiter")]
    public string? Delimiter { get; set; }

    // ---------- Digits ----------
    [JsonPropertyName("individual_digits")]
    public bool? IndividualDigits { get; set; }

    // ---------- Metaspace ----------
    [JsonPropertyName("replacement")]
    public string? Replacement { get; set; }

    [JsonPropertyName("prepend_scheme")]
    public string? PrependScheme { get; set; }

    // ---------- Strip (pre_tokenizer) ----------
    [JsonPropertyName("lstrip")]
    public bool? Lstrip { get; set; }

    [JsonPropertyName("rstrip")]
    public bool? Rstrip { get; set; }

    // ---------- Replace (pre_tokenizer) ----------
    [JsonPropertyName("content")]
    public string? ReplaceContent { get; set; }
}

/// <summary>HuggingFace tokenizer.json 的 <c>normalizer</c> 节点。</summary>
public class NormalizerConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Sequence";

    [JsonPropertyName("normalizers")]
    public List<NormalizerItem>? Normalizers { get; set; }
}

public class NormalizerItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("normalizers")]
    public List<NormalizerItem>? Normalizers { get; set; }

    [JsonPropertyName("pattern")]
    [JsonConverter(typeof(SplitPatternJsonConverter))]
    public string? PatternExpression { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("left")]
    public bool? Left { get; set; }

    [JsonPropertyName("right")]
    public bool? Right { get; set; }

    // BertNormalizer
    [JsonPropertyName("clean_text")]
    public bool? CleanText { get; set; }

    [JsonPropertyName("handle_chinese_chars")]
    public bool? HandleChineseChars { get; set; }

    [JsonPropertyName("strip_accents")]
    public bool? StripAccents { get; set; }

    [JsonPropertyName("lowercase")]
    public bool? Lowercase { get; set; }
}

public class TokenizerJson_MergesAsString
{
    [JsonPropertyName("model")]
    public Model_MergesAsString Model { get; set; }

    [JsonPropertyName("normalizer")]
    public NormalizerConfig? Normalizer { get; set; }

    [JsonPropertyName("added_tokens")]
    public List<AddedToken> AddedTokens { get; set; }

    public Dictionary<int, string> SpecialTokens() =>
       AddedTokens.ToDictionary(at => at.Id, at => at.Content);

    [JsonPropertyName("pre_tokenizer")]
    public PreTokenizerConfig? PreTokenizerConfig { get; set; }
}

public class Model_MergesAsString
{
    [JsonPropertyName("vocab")]
    public Dictionary<string, int> Vocab { get; set; }

    [JsonPropertyName("merges")]          // ←补上
    public List<string> Merges { get; set; }
}

public static class TokMap
{
    /// <summary>
    /// 检测 tokenizer.json 中 <c>model.merges</c> 的两种常见格式：
    /// HuggingFace 多为 <c>["a b", ...]</c>（字符串），部分导出为 <c>[["a","b"], ...]</c>（二维数组）。
    /// </summary>
    private static bool MergesAreStringLines(JsonElement modelElement)
    {
        if (!modelElement.TryGetProperty("merges", out var merges) || merges.ValueKind != JsonValueKind.Array)
            return false;
        if (merges.GetArrayLength() == 0)
            return false;
        return merges[0].ValueKind == JsonValueKind.String;
    }

    public static (string[] bpeVocabLines,
                   Dictionary<string, int> encoder,
                   Dictionary<int, string> special,
                   NormalizerConfig? normalizer,
                   PreTokenizerConfig? preTokenizer)
          LoadFromTokenizerJson(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("model", out var modelEl))
            throw new InvalidOperationException("tokenizer.json 缺少 \"model\" 节点。");

        if (MergesAreStringLines(modelEl))
        {
            var tok = JsonSerializer.Deserialize<TokenizerJson_MergesAsString>(root)
                ?? throw new InvalidOperationException("反序列化 TokenizerJson_MergesAsString 失败。");
            return BuildLoadResult_StringMerges(tok);
        }

        var tokPair = JsonSerializer.Deserialize<TokenizerJson>(root)
            ?? throw new InvalidOperationException("反序列化 TokenizerJson 失败。");
        return BuildLoadResult_PairMerges(tokPair);
    }

    private static (string[] bpeVocabLines, Dictionary<string, int> encoder, Dictionary<int, string> special, NormalizerConfig? normalizer, PreTokenizerConfig? preTokenizer)
        BuildLoadResult_PairMerges(TokenizerJson tok)
    {
        // 严格校验：tokenizer.json 必含 model.vocab 与 model.merges（且 merges 元素必须是 2-tuple），
        // added_tokens 可缺省但若存在则元素必须有 content 字段。任何残缺立即抛 InvalidOperationException
        // 指明具体字段，避免后续 NRE/越界。
        if (tok is null)
            throw new InvalidOperationException("tokenizer.json 反序列化失败：根对象为空。");
        if (tok.Model is null)
            throw new InvalidOperationException("tokenizer.json 缺少 \"model\" 节点。");
        if (tok.Model.Vocab is null || tok.Model.Vocab.Count == 0)
            throw new InvalidOperationException("tokenizer.json \"model.vocab\" 节点缺失或为空。");
        if (tok.Model.Merges is null)
            throw new InvalidOperationException("tokenizer.json \"model.merges\" 节点缺失。");

        var enc = tok.Model.Vocab;
        var special = BuildOrderedSpecialTokens(tok.AddedTokens);

        var mergeLines = new string[tok.Model.Merges.Count];
        for (int i = 0; i < tok.Model.Merges.Count; i++)
        {
            var p = tok.Model.Merges[i];
            if (p is null || p.Count != 2)
                throw new InvalidOperationException($"tokenizer.json \"model.merges\"[{i}] 应为长度为 2 的字符串数组。");
            mergeLines[i] = $"{p[0]} {p[1]}";
        }
        return (mergeLines, enc, special, tok.Normalizer, tok.PreTokenizerConfig);
    }

    private static (string[] bpeVocabLines, Dictionary<string, int> encoder, Dictionary<int, string> special, NormalizerConfig? normalizer, PreTokenizerConfig? preTokenizer)
        BuildLoadResult_StringMerges(TokenizerJson_MergesAsString tok)
    {
        if (tok is null)
            throw new InvalidOperationException("tokenizer.json 反序列化失败：根对象为空。");
        if (tok.Model is null)
            throw new InvalidOperationException("tokenizer.json 缺少 \"model\" 节点。");
        if (tok.Model.Vocab is null || tok.Model.Vocab.Count == 0)
            throw new InvalidOperationException("tokenizer.json \"model.vocab\" 节点缺失或为空。");
        if (tok.Model.Merges is null)
            throw new InvalidOperationException("tokenizer.json \"model.merges\" 节点缺失。");

        var enc = tok.Model.Vocab;
        var special = BuildOrderedSpecialTokens(tok.AddedTokens);
        var mergeLines = new string[tok.Model.Merges.Count];
        for (int i = 0; i < tok.Model.Merges.Count; i++)
        {
            var s = tok.Model.Merges[i];
            if (string.IsNullOrEmpty(s))
                throw new InvalidOperationException($"tokenizer.json \"model.merges\"[{i}] 为空字符串。");
            var p = s.Split(' ', 2, StringSplitOptions.None);
            if (p.Length != 2)
                throw new InvalidOperationException($"无效的 merge 行（需恰好两个由空格分隔的片段）: \"{s}\"");
            mergeLines[i] = $"{p[0]} {p[1]}";
        }
        return (mergeLines, enc, special, tok.Normalizer, tok.PreTokenizerConfig);
    }

    /// <summary>
    /// 将 added_tokens 列表转为「按 id 索引」的字典；保留原始顺序（用于后续 splitter 长度降序排序）；
    /// 重复 id 时取最后一条（与「明确报错」相比，更兼容个别 HF 导出工具的怪异输出）。
    /// </summary>
    private static Dictionary<int, string> BuildOrderedSpecialTokens(List<AddedToken>? addedTokens)
    {
        var result = new Dictionary<int, string>(addedTokens?.Count ?? 0);
        if (addedTokens is null) return result;
        for (int i = 0; i < addedTokens.Count; i++)
        {
            var at = addedTokens[i];
            if (at is null) continue;
            if (string.IsNullOrEmpty(at.Content))
                continue;
            result[at.Id] = at.Content;
        }
        return result;
    }
}

