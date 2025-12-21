// P1_ChineseSubwordTests.cs
using LumTokenizer.Tokenizer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace MiniMind.Tokenizer.Tests
{
    [TestClass]
    public class P3_ChineseSubwordTests
    {
        private static BPETokenizer _tok;

        [ClassInitialize]
        public static void Init(TestContext _) =>
            _tok = BPETokenizer.CreateTokenizer(
                  @"D:\Data\Personal\AI\llm\tokenizer\minimind_tokenizer.txt");

        /* 工具：把 id 转成可视 token */
        private static List<string> ToTokens(IList<int> ids)
            => ids.Select(i => _tok.Decode([i]))
                  .ToList();

        /* ---------- 01 常用双字词必须合并 ---------- */
        [TestMethod]
        public void Chinese_CommonTwoCharWord()
        {
            var words = new[] { "你好", "中国", "北京", "中国", "天气" };
            foreach (var w in words)
            {
                var ids = _tok.Encode(w);
                // 必须出现长度≥4 的 token（UTF-16 两汉字=4 char）
                Assert.IsTrue(ids.Count==1,
                              $"{w} 未被合并，{w}");
            }
        }

        /* ---------- 02 单字率 < 100 % ---------- */
        [TestMethod]
        public void Chinese_SingleCharRate()
        {
            var sentence = "我爱北京天安门，天气很好。";
            var ids = _tok.Encode(sentence);
            var tokens = ToTokens(ids);
            int singleCount = tokens.Count(t => t.Length == 1);   // 单字
            double rate = (double)singleCount / tokens.Count;
            Console.WriteLine($"[SingleCharRate] {rate:P2}  (tokens=[{string.Join("|", tokens)}])");
            // 子词 tokenizer 单字率应 < 70 %
            Assert.IsTrue(rate < 0.75, $"单字率过高：{rate:P2}");
        }

        /* ---------- 03 总 token 数 < 字符数 ---------- */
        [TestMethod]
        public void Chinese_TokenCount_LessThanCharCount()
        {
            var sentence = "人工智能正在改变世界。";
            int charCount = sentence.Length;                 // 汉字+标点
            int tokenCount = _tok.Encode(sentence).Count;
            Console.WriteLine($"[TokenCount] 汉字+标点={charCount}, token={tokenCount}");
            // 子词应比字符级少
            Assert.IsTrue(tokenCount < charCount,
                          $"token 数未少于字符数：{tokenCount} >= {charCount}");
        }

        /* ---------- 04 多字词合并 ---------- */
        [TestMethod]
        public void Chinese_MultiCharWord()
        {
            var word = "对不起";
            var ids = _tok.Encode(word);
            // 一个汉字2个token
            Assert.IsTrue(ids.Count <6,
                          $"{word} 未形成多字子词");
        }

        /* ---------- 05 回归：整句 round-trip ---------- */
        [TestMethod]
        public void Chinese_RoundTrip()
        {
            var original = "我爱机器学习，人工智能真神奇！";
            var ids = _tok.Encode(original);
            var restored = _tok.Decode(ids);
            Assert.AreEqual(original, restored);
        }
        
        /* ---------- 05 回归：整句 round-trip ---------- */
        [TestMethod]
        public void Chinese_Rare()
        {
            var original = "嘜";
            var ids = _tok.Encode(original);
            Assert.AreEqual(ids.Count,2);

            var restored = _tok.Decode(ids);
            Assert.AreEqual(original, restored);
        }
    }
}