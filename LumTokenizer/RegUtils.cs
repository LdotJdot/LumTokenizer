using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LumTokenizer.RegexExpression
{

    public enum RegexType
    {
        /// <summary>
        /// 从配置文件读取自定义正则表达式
        /// </summary>
        Custom,

        /// <summary>
        /// 50K 基础版（类似 GPT-2）
        /// </summary>
        Regex50KBase,
        /// <summary>
        /// CL-100K 基础版（类似 OpenAI 的 CLIP、GPT-3.5、GPT-4 等）
        /// </summary>
        RegexCl100KBase,
        /// <summary>
        /// O-200K 基础版（类似 Meta 的 LLaMA、Mistral 等）
        /// </summary>
        RegexO200KBase,

    }
    internal static class RegUtils
    {
        public static Regex GetRegex(RegexType type)
        {
            return type switch
            {
                RegexType.Regex50KBase => Regex50KBase(),
                RegexType.RegexCl100KBase => RegexCl100KBase(),
                RegexType.RegexO200KBase => RegexO200KBase(),
                _ => throw new NotSupportedException($"不支持的 RegexType：{type}"),
            };
        }
        public static Regex Regex50KBase() => new Regex(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+", RegexOptions.Compiled);

        public static Regex RegexCl100KBase() => new Regex(@"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+", RegexOptions.Compiled);

        public static Regex RegexO200KBase() => new Regex(@"[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]*[\p{Ll}\p{Lm}\p{Lo}\p{M}]+(?i:'s|'t|'re|'ve|'m|'ll|'d)?|[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]+[\p{Ll}\p{Lm}\p{Lo}\p{M}]*(?i:'s|'t|'re|'ve|'m|'ll|'d)?|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n/]*|\s*[\r\n]+|\s+(?!\S)|\s+", RegexOptions.Compiled);

    }
}
