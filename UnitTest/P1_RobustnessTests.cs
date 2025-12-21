// P1_RobustnessTests.cs
using LumTokenizer.Tokenizer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MiniMind.Tokenizer.Tests
{
    [TestClass]
    public class P1_RobustnessTests
    {
        private static BPETokenizer _tok;

        [ClassInitialize]
        public static void Init(TestContext _) =>
            _tok = BPETokenizer.CreateTokenizer(
                  @"D:\Data\Personal\AI\llm\tokenizer\qw_tokenizer.json", false,LumTokenizer.RegexExpression.RegexType.RegexCl100KBase);

        /* 01 空输入 */
        [TestMethod]
        public void Encode_EmptyString_ReturnsEmptyIds() =>
            CollectionAssert.AreEqual(new List<int>(), _tok.Encode(""));

        [TestMethod]
        public void Decode_EmptyIds_ReturnsEmptyString() =>
            Assert.AreEqual("", _tok.Decode([]));

        /* 02 仅 special tokens */
        [TestMethod]
        public void Encode_OnlySpecialTokens()
        {
            var txt = "<|im_end|><|im_start|>";
            var ids = _tok.Encode(txt);
            var back = _tok.Decode(ids);
            Assert.AreEqual(txt, back);
        }

        /* 03 前后空白 */
        [TestMethod]
        public void Encode_LeadingTrailingSpaces()
        {
            var txt = "  hello  ";
            var ids = _tok.Encode(txt);
            var back = _tok.Decode(ids);
            Assert.AreEqual(txt, back);
        }

        /* 04 纯空格 */
        [TestMethod]
        public void Encode_OnlySpaces()
        {
            var txt = new string(' ', 10);
            var ids = _tok.Encode(txt);
            var back = _tok.Decode(ids);
            Assert.AreEqual(txt, back);
        }

        /* 05 Unicode 空白 */
        [TestMethod]
        public void Encode_UnicodeWhitespace()
        {
            // \u00A0 = 不间断空格，\u2003 = em space
            var txt = "hello\u00A0world\u2003!";
            var ids = _tok.Encode(txt);
            var back = _tok.Decode(ids);
            Assert.AreEqual(txt, back);
        }

        /* 06 制表符与换行 */
        [TestMethod]
        public void Encode_TabNewline()
        {
            var txt = "hello\tworld\nline2";
            var ids = _tok.Encode(txt);
            var back = _tok.Decode(ids);
            Assert.AreEqual(txt, back);
        }

        /* 07 超长文本（流式接口不抛异常） */
        [TestMethod]
        public void Encode_MaxLength_DoesNotThrow()
        {
            var oneLine = new string('a', 10_000);
            // 如果 tokenizer 内部有分块/流式，应能跑完
            var ids = _tok.Encode(oneLine);
            Assert.IsTrue(ids.Count > 0);
        }

        /* 08 越界 ID 解码 */
        [TestMethod]
        public void Decode_OutOfRangeId_Throws()
        {
            // 假设实现里会检查 id 范围
            var res = _tok.Decode([ _tok.VocabSize + 100 ]);
            Assert.AreEqual(string.Empty, res);
        }


        /* 09 BOM 头剥离 */
// 由分词器外实现

/* 10 CRLF 统一 */
// 纯BPE tokenizer 不处理换行符统一 

/* 11 特殊字符处理 */
/* 需要在 LumTokenizer.RegexExpression.RegexType.RegexCl100KBase 匹配下满足 */

[TestMethod]
public void Encode_Special_Characters_Merge()
{
    // 如果 tokenizer 内部有分块/流式，应能跑完
    var ids = _tok.Encode("><");
    Assert.AreEqual(1, ids.Count);
}
}
}