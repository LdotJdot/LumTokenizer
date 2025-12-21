using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public class TokenizerJson
{
    [JsonPropertyName("model")]
    public Model Model { get; set; }
    
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
/// 对应 tiktoken 的 pre_tokenizer 节点
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

    // ---------- Split 专用 ----------
    [JsonPropertyName("pattern")]
    public PatternInfo? Pattern { get; set; }

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
}

/// <summary>
/// 只包装一个字符串，让 JSON 仍然直接序列化成字符串
/// </summary>
public class PatternInfo
{
    [JsonPropertyName("Regex")]
    public string Regex { get; set; } = string.Empty;

    public static implicit operator PatternInfo(string regex) => new() { Regex = regex };
    public static implicit operator string(PatternInfo p) => p.Regex;
}

public class TokenizerJson_MergesAsString
{
    [JsonPropertyName("model")]
    public Model_MergesAsString Model { get; set; }

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
    public static (string[] bpeVocabLines,
                   Dictionary<string, int> encoder,
                   Dictionary<int, string> special,
                   string regex)
          LoadFromTokenizerJson(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var tok = JsonSerializer.Deserialize<TokenizerJson>(json);

        var enc = tok.Model.Vocab;
        var special = tok.SpecialTokens();

        // 真正的 merges 行：把二维数组还原成 "left right" 格式
        var mergeLines = new List<string>();
        mergeLines.AddRange(tok.Model.Merges.Select(p => $"{p[0]} {p[1]}"));

        string regex = tok?.PreTokenizerConfig?
               .Pretokenizers?.FirstOrDefault(o => o.Type.Equals("Split", StringComparison.OrdinalIgnoreCase))?
               .Pattern?.Regex ?? string.Empty;
        return (mergeLines.ToArray(), enc, special, regex);

    }


    public static (string[] bpeVocabLines,
                   Dictionary<string, int> encoder,
                   Dictionary<int, string> special,
                   string regex)
          LoadFromTokenizerJson_MergesAsString(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var tok = JsonSerializer.Deserialize<TokenizerJson_MergesAsString>(json);

        var enc = tok.Model.Vocab;
        var special = tok.SpecialTokens();

        // 真正的 merges 行：把二维数组还原成 "left right" 格式
        var mergeLines = new List<string>();
        mergeLines.AddRange(tok.Model.Merges.Select(s=>s.Split(' ')).Select(p => $"{p[0]} {p[1]}"));

        string regex = tok?.PreTokenizerConfig?
            .Pretokenizers?.FirstOrDefault(o => o.Type.Equals("Split", StringComparison.OrdinalIgnoreCase))?
            .Pattern?.Regex??string.Empty;

        return (mergeLines.ToArray(), enc, special,regex);
    }
}

