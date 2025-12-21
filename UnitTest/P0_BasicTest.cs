using LumTokenizer.Tokenizer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MiniMind.Tokenizer.Tests
{
    [TestClass]
    public class TokenizerTests
    {
        private const string VocabPath = @"D:\Data\Personal\AI\llm\tokenizer\minimind_tokenizer.txt";
        private static BPETokenizer _tokenizer;

        [ClassInitialize]
        public static void ClassInit(TestContext _)
        {
            _tokenizer = BPETokenizer.CreateTokenizer(VocabPath); // 换成你的工厂方法
        }

        #region Special Token
        [TestMethod]
        public void Encode_SpecialToken_WholePiece()
        {
            var text = "<|im_start|>hello<|im_end|>";
            var ids = _tokenizer.Encode(text);
            CollectionAssert.Contains(ids, _tokenizer.Encode("<|im_start|>")[0]);
            CollectionAssert.Contains(ids, _tokenizer.Encode("<|im_end|>")[0]);
            // 不应拆成单字符
            Assert.AreNotEqual("<", _tokenizer.Decode([ids[0]]));
        }

        [TestMethod]
        public void Decode_SpecialToken_RoundTrip()
        {
            var original = "<|im_start|>hello  你好<|im_end|>";
            var ids = _tokenizer.Encode(original);
            var restored = _tokenizer.Decode(ids);
            Assert.AreEqual(original, restored);
        }
        #endregion

        #region Regular Text
        [TestMethod]
        public void EncodeDecode_English()
        {
            var text = "Hello world";
            var ids = _tokenizer.Encode(text);
            var restored = _tokenizer.Decode(ids);
            Assert.AreEqual(text, restored);
        }

        [TestMethod]
        public void EncodeDecode_Chinese()
        {
            var text = "你好，世界";
            var ids = _tokenizer.Encode(text);
            var restored = _tokenizer.Decode(ids);
            Assert.AreEqual(text, restored);
        }
        #endregion

        #region Space Normalization
        //[TestMethod]
        //public void Encode_ConsecutiveSpaces_Collapsed()
        //{
        //    var text = "hello   world";          // 3 空格
        //    var ids = _tokenizer.GetTokens(text);
        //    var restored = _tokenizer.GetTextFromTokens(ids);
        //    // 期望归一为 1 空格
        //    Assert.AreEqual("hello world", restored);
        //}
        #endregion

        #region Edge Cases
        [TestMethod]
        public void Encode_EmptyString_ReturnsEmptyList()
        {
            var ids = _tokenizer.Encode("");
            Assert.AreEqual(0, ids.Count);
        }

        [TestMethod]
        public void Encode_OnlySpecialTokens()
        {
            var text = "<|im_end|><|im_start|>";
            var ids = _tokenizer.Encode(text);
            var restored = _tokenizer.Decode(ids);
            Assert.AreEqual(text, restored);
        }
        #endregion

        #region Vocab & Embedding Alignment
        [TestMethod]
        public void VocabSize_MatchesEmbeddingAfterResize()
        {
            // 假设你后面会 resize 模型 embedding，这里先断言当前一致性
            int vocabSize = _tokenizer.VocabSize;
            // 若同时持有模型实例，可反查：
            // Assert.AreEqual(vocabSize, model.EmbeddingLayer.VocabSize);
            Assert.IsTrue(vocabSize > 1000); // 任意合理性检查
        }
        #endregion
    }
}