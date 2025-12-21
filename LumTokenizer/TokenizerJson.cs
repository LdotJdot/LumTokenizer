using System.Text.Json;
using System.Text.Json.Serialization;

public class TokenizerJson
{
    [JsonPropertyName("model")]
    public Model Model { get; set; }

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


public class TokenizerJson_MergesAsString
{
    [JsonPropertyName("model")]
    public Model_MergesAsString Model { get; set; }

    [JsonPropertyName("added_tokens")]
    public List<AddedToken> AddedTokens { get; set; }

    public Dictionary<int, string> SpecialTokens() =>
       AddedTokens.ToDictionary(at => at.Id, at => at.Content);
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
                   Dictionary<int, string> special)
          LoadFromTokenizerJson(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var tok = JsonSerializer.Deserialize<TokenizerJson>(json);

        var enc = tok.Model.Vocab;
        var special = tok.SpecialTokens();

        // 真正的 merges 行：把二维数组还原成 "left right" 格式
        var mergeLines = new List<string>
        {
            "#version: 1.0"                 // 与原始 merges.txt 保持一致
        };
        mergeLines.AddRange(tok.Model.Merges.Select(p => $"{p[0]} {p[1]}"));

        return (mergeLines.ToArray(), enc, special);
    }


    public static (string[] bpeVocabLines,
                   Dictionary<string, int> encoder,
                   Dictionary<int, string> special)
          LoadFromTokenizerJson_MergesAsString(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var tok = JsonSerializer.Deserialize<TokenizerJson_MergesAsString>(json);

        var enc = tok.Model.Vocab;
        var special = tok.SpecialTokens();

        // 真正的 merges 行：把二维数组还原成 "left right" 格式
        var mergeLines = new List<string>
        {
            "#version: 1.0"                 // 与原始 merges.txt 保持一致
        };
        mergeLines.AddRange(tok.Model.Merges.Select(s=>s.Split(' ')).Select(p => $"{p[0]} {p[1]}"));

        return (mergeLines.ToArray(), enc, special);
    }
}

