using LumTokenizer.Tokenizer;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace LumTokenizer
{
    public class HighPerformanceSpanSplitter
    {
        private readonly HashSet<string> _keywords;
        private readonly int _maxKeywordLength;
        private readonly HashSet<char> _firstChars;
        private readonly FrozenDictionary<string, bool> _keywordMap;

        public HighPerformanceSpanSplitter(IEnumerable<string> keywords)
        {
            if (keywords == null) throw new ArgumentNullException(nameof(keywords));

            _keywords = new HashSet<string>();
            _maxKeywordLength = 0;
            _firstChars = new HashSet<char>();
            
            var keywordMap = new Dictionary<string, bool>();

            foreach (var keyword in keywords)
            {
                if (!string.IsNullOrEmpty(keyword))
                {
                    _keywords.Add(keyword);
                     keywordMap[keyword] = true;
                    _maxKeywordLength = Math.Max(_maxKeywordLength, keyword.Length);
                    if (keyword.Length > 0)
                        _firstChars.Add(keyword[0]);
                }
            }

            _keywordMap = keywordMap.ToFrozenDictionary();
        }

        public void Split(SpanStringCollection input)
        {

            if (string.IsNullOrEmpty(input.Origin)) return;

            var span = input.Origin.AsSpan();
                
            int currentIndex = 0;
            int inputLength = span.Length;
            int textStart = 0;

            while (currentIndex < inputLength)
            {
                bool found = false;

                char firstChar = span[currentIndex];
                if (_firstChars.Contains(firstChar))
                {
                    int maxLength = Math.Min(_maxKeywordLength, inputLength - currentIndex);

                    // 优先尝试最长匹配
                    for (int length = maxLength; length >= 1; length--)
                    {
                        var candidate = span.Slice(currentIndex, length);

                        // 快速检查
                        if (length > 0 && _firstChars.Contains(candidate[0]))
                        {
                            // 使用自定义比较或转换为string
                            string candidateStr = candidate.ToString();

                            if (_keywordMap.ContainsKey(candidateStr))
                            {
                                // 添加文本段
                                if (currentIndex > textStart)
                                {
                                    input.Add(new Range(textStart, currentIndex));
                                }

                                // 添加关键词段
                                input.Add(new Range(currentIndex, currentIndex + length));

                                // 更新位置
                                currentIndex += length;
                                textStart = currentIndex;
                                found = true;
                                break;
                            }
                        }
                    }
                }

                if (!found)
                {
                    currentIndex++;
                }
            }

            // 最后一段文本
            if (textStart < inputLength)
            {
                input.Add(new Range(textStart, inputLength));
            }            
        }
    }
}
