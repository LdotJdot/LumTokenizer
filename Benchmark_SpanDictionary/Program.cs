using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using LumTokenizer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;


[MemoryDiagnoser]
public class HtmlAttributeParserBenchmark
{
    private const string HtmlSample = @"<div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value""><div class=""container"" id=""main"" data-user=""123"" data-role=""admin"" style=""color:red"" title=""测试"" data-custom=""value"">";

    private static readonly Dictionary<string, string> StringDict = new()
    {
        ["class"] = "css类",
        ["id"] = "元素ID",
        ["data-user"] = "用户ID",
        ["data-role"] = "用户角色",
        ["style"] = "内联样式",
        ["title"] = "标题"
    };

    private static readonly SpanDictionary<string> SpanDict = new();

    static HtmlAttributeParserBenchmark()
    {
        // 初始化SpanDictionary
        SpanDict.Add("class", "css类");
        SpanDict.Add("id", "元素ID");
        SpanDict.Add("data-user", "用户ID");
        SpanDict.Add("data-role", "用户角色");
        SpanDict.Add("style", "内联样式");
        SpanDict.Add("title", "标题");
    }

    [Benchmark(Baseline = true)]
    public int ParseWithStringDict()
    {
        int matchCount = 0;
        ReadOnlySpan<char> html = HtmlSample.AsSpan();

        // 简化的属性解析：提取属性名
        int start = 4; // 跳过 "<div"
        while (start < html.Length)
        {
            int eqPos = html.Slice(start).IndexOf('=');
            if (eqPos == -1) break;
            eqPos += start;

            int spacePos = html.Slice(0, eqPos).LastIndexOf(' ');
            if (spacePos == -1) spacePos = start - 1;

            int nameStart = spacePos + 1;
            int nameLength = eqPos - nameStart;

            // Span方式：零分配子串
            ReadOnlySpan<char> attrName = html.Slice(nameStart, nameLength);

            if (StringDict.TryGetValue(attrName.ToString(), out _))
            {
                matchCount++;
            }

            int nextSpace = html.Slice(eqPos).IndexOf(' ');
            if (nextSpace == -1) break;
            start = eqPos + nextSpace;
        }
        return matchCount;
    }

    [Benchmark]
    public int ParseWithSpanDict()
    {
        int matchCount = 0;
        ReadOnlySpan<char> html = HtmlSample.AsSpan();

        // 简化的属性解析：提取属性名
        int start = 4; // 跳过 "<div"
        while (start < html.Length)
        {
            int eqPos = html.Slice(start).IndexOf('=');
            if (eqPos == -1) break;
            eqPos += start;

            int spacePos = html.Slice(0, eqPos).LastIndexOf(' ');
            if (spacePos == -1) spacePos = start - 1;

            int nameStart = spacePos + 1;
            int nameLength = eqPos - nameStart;

            // Span方式：零分配子串
            ReadOnlySpan<char> attrName = html.Slice(nameStart, nameLength);

            if (SpanDict.TryGetValue(attrName, out _))
            {
                matchCount++;
            }

            int nextSpace = html.Slice(eqPos).IndexOf(' ');
            if (nextSpace == -1) break;
            start = eqPos + nextSpace;
        }

        return matchCount;
    }
}

public class Program
{
    unsafe public static void Main(string[] args)
    {
        BenchmarkRunner.Run<HtmlAttributeParserBenchmark>();
    }


}