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
        var enc = tok.Model.Vocab;
        var special = tok.SpecialTokens();
        var mergeLines = tok.Model.Merges.Select(p => $"{p[0]} {p[1]}").ToArray();
        return (mergeLines, enc, special, tok.Normalizer, tok.PreTokenizerConfig);
    }

    private static (string[] bpeVocabLines, Dictionary<string, int> encoder, Dictionary<int, string> special, NormalizerConfig? normalizer, PreTokenizerConfig? preTokenizer)
        BuildLoadResult_StringMerges(TokenizerJson_MergesAsString tok)
    {
        var enc = tok.Model.Vocab;
        var special = tok.SpecialTokens();
        var mergeLines = tok.Model.Merges
            .Select(s =>
            {
                var p = s.Split(' ', 2, StringSplitOptions.None);
                if (p.Length != 2)
                    throw new InvalidOperationException($"无效的 merge 行（需恰好两个由空格分隔的片段）: \"{s}\"");
                return $"{p[0]} {p[1]}";
            })
            .ToArray();
        return (mergeLines, enc, special, tok.Normalizer, tok.PreTokenizerConfig);
    }
}

