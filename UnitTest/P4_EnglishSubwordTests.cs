// P1_EnglishSubwordTests.cs  （修正版）
using LumTokenizer.Tokenizer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;

namespace MiniMind.Tokenizer.Tests
{
    [TestClass]
    public class P4_EnglishSubwordTests
    {
        private static BPETokenizer _tok;
        [ClassInitialize]
        public static void Init(TestContext _) =>
            _tok = BPETokenizer.CreateTokenizer(
                  @"D:\Data\Personal\AI\llm\tokenizer\minimind_tokenizer.txt");

        private static List<string> ToTokens(IList<int> ids) =>
            ids.Select(i => _tok.Decode([i])).ToList();

        /* 01 后缀合并效果（字符级验证） */
        [TestMethod]
        public void English_SuffixMerged()
        {
            var word = "running";
            var tokens = ToTokens(_tok.Encode(word));
            // 必须出现 *ing 片段，且总 token 数 < 字符数
            Assert.IsTrue(tokens.Any(t => t.EndsWith("ing")),
                          $"missing *ing in {string.Join("|", tokens)}");
            Assert.IsTrue(tokens.Count < word.Length, "no compression at all");
        }

        /* 02 单字符率（字符级） < 60 % */
        [TestMethod]
        public void English_SingleCharRate()
        {
            var sentence = "I am running faster than you.";
            var tokens = ToTokens(_tok.Encode(sentence));
            int single = tokens.Count(t => t.Length == 1);   // 单字符 token
            double rate = (double)single / tokens.Count;
            Console.WriteLine($"[English] single-char rate = {rate:P2}");
            Assert.IsTrue(rate < 0.6, $"too many single chars: {rate:P2}");
        }

        /* 03 总 token 数 < 总字符数 （核心：压缩率 > 1.0） */
        [TestMethod]
        public void English_TokenCount_LessThanCharCount()
        {
            var sentence = "Artificial intelligence is transforming our world.";
            int charCount = sentence.Length;
            int tokenCount = _tok.Encode(sentence).Count;
            Console.WriteLine($"[English] chars={charCount}, tokens={tokenCount}, ratio={charCount / (double)tokenCount:F2}");
            Assert.IsTrue(tokenCount < charCount,
                          $"no compression: tokens({tokenCount}) >= chars({charCount})");
        }

        
        /* 04 整句 round-trip（字符级） */
        [TestMethod]
        public void English_RoundTrip()
        {
            var original = "I love machine learning and deep learning!";
            var restored = _tok.Decode(_tok.Encode(original));
            Assert.AreEqual(original, restored);
        }

        /* 05 前缀空格 映射 */
        [TestMethod]
        public void English_PrefixSpace_DistinctToken()
        {
            var withSpace = " app";      // 前面有空格
            var noSpace = "app";       // 前面无空格

            var ids1 = _tok.Encode(withSpace);
            var ids2 = _tok.Encode(noSpace);

            // 都是 1 个 token
            Assert.AreEqual(1, ids1.Count);
            Assert.AreEqual(1, ids2.Count);

            // 但 id 不同
            Assert.AreNotEqual(ids1[0], ids2[0],
                "\" app\" 和 \"app\" 应该映射到不同 token");

            // decode 后能还原原始空格
            var restored1 = _tok.Decode(ids1);
            var restored2 = _tok.Decode(ids2);

            Assert.AreEqual(withSpace, restored1);
            Assert.AreEqual(noSpace, restored2);
        }
    }
}