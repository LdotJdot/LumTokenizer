using LumTokenizer.Tokenizer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MiniMind.Tokenizer.Tests
{
    [TestClass]
    public class P2_VocabBpeTests
    {
        private static BPETokenizer _tok;

        [ClassInitialize]
        public static void Init(TestContext _) =>
            _tok = BPETokenizer.CreateTokenizer(
                  @"D:\Data\Personal\AI\llm\tokenizer\minimind_tokenizer.txt");

        /* ---------- 01 未知/byte-fallback ---------- */
        [TestMethod]
        public void Encode_UnknownToken_ReturnsUnkOrByteFallback()
        {
            // 选一个肯定不在词表的 Emoji
            var txt = "hello😊world";
            var ids = _tok.Encode(txt);
            // 要么走 <unk>，要么走 byte-fallback；不允许抛异常
            Assert.IsTrue(ids.Count > 0);
            // 解码后应该能回来（byte-fallback 可逆）
            var restored = _tok.Decode(ids);
            Assert.AreEqual(txt, restored);
        }

        /* ---------- 02 全词表 round-trip ---------- */
        //单向性考虑，不是所有词表项都能还原回原始文本

        /* ---------- 03 最长优先 ---------- */
        [TestMethod]
        public void Encode_GreedyLongestFirst()
        {
            // 词表假设：有 "ab"、"bc"，没有 "abc"
            var txt = "abc";
            var ids = _tok.Encode(txt);
            var tokens = _tok.Decode(ids);   // 转可视字符串

            // 最长优先应合并成 2 个 token： "ab" + "c"  或  "a" + "bc"
            Console.WriteLine($"tokens=[{string.Join("|", tokens)}]");
            Assert.AreEqual(2, ids.Count,
                            $"最长优先失败，token 数={ids.Count}，tokens=[{string.Join("|", tokens)}]");
        }

        /* ---------- 04 稳定性 ---------- */
        [TestMethod]
        public void Encode_StableHash()
        {
            var txt = "<|im_start|>hello  你好<|im_end|>";
            var ids1 = _tok.Encode(txt);
            var ids2 = _tok.Encode(txt);
            CollectionAssert.AreEqual(ids1, ids2);
        }

        /* ---------- 05 极端空格 ---------- */
        [TestMethod]
        public void RoundTrip_LongSpaces()
        {
            var txt = "hello" + new string(' ', 20) + "world";
            var ids = _tok.Encode(txt);
            var restored = _tok.Decode(ids);
            Assert.AreEqual(txt, restored);
        }
    }
}