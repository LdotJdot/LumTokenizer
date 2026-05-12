using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Iced.Intel;
using LumTokenizer;
using LumTokenizer.Tokenizer;
using Microsoft.Diagnostics.Symbols;
using SharpToken;
using System.Buffers;
using System.Diagnostics;
using TiktokenSharp;

namespace ConsoleApp1
{

    internal class Program
    {
        // 固定的 magic number，就是你已经测出来的 token
        static readonly int[] userStart = { 1, 320, 275 };          // <|im_start|>user
        static readonly int[] assistantStart = { 1, 1078, 538, 501 };    // <|im_start|>assistant
        static readonly int eos = 2;                                     // <|im_end|>
        static (int[] inputIds, int[] labels) MakeSftPair(List<int> fullTokens)
        {
            int len = fullTokens.Count;
            int[] inputIds = fullTokens.ToArray();
            int[] labels = new int[len];

            // 1. 全部 mask
            Array.Fill(labels, -100);

            var assistantStartSpan = assistantStart.AsSpan();
            var aspLength = assistantStartSpan.Length;
            var inputIdsSpan = inputIds.AsSpan();
            var labelsSpan = labels.AsSpan();

            // 2. 扫描 assistant 区间
            for (int i = 1; i < len;)
            {
                // 探测 <|im_start|>assistant
                if (i + aspLength <= len && assistantStartSpan.SequenceEqual(inputIdsSpan.Slice(i, aspLength)))
                {                    
                    int segStart = i;

                    // 一直扫到、并包含 <|im_end|>
                    while (i < len && inputIds[i] != eos) i++;

                    while (i<len && (inputIds[i] == 0 || inputIds[i]==eos)) i++;

                    var segLen = i - segStart;

                    inputIdsSpan.Slice(segStart, segLen).CopyTo(labelsSpan.Slice(segStart-1, segLen));
                }
                else
                    i++;
            }
            return (inputIds, labels);
        }

        static void Main(string[] args)
        {


            //var _tokenizer2 = ConcurrentBPETokenizer.CreateTokenizer(
            //    @"D:\Data\Personal\AI\llm\tokenizer\minimind_tokenizer.txt", false);
            var _tokenizer2 = ConcurrentBPETokenizer.CreateTokenizer(
                @"D:\Data\个人\FAV\MyCoreProj\Projects\OpenLumSharp\OpenLum.WebHost\bin\Debug\net10.0\TokenizerProfiles\deepseek-v4-flash\tokenizer.json");
            var str = "<｜begin▁of▁sentence｜>今天天气不错<｜end▁of▁sentence｜>";
            var id = _tokenizer2.Encode(str);
            var res = MakeSftPair(id);
            Console.WriteLine(string.Join(',', res.inputIds));
            Console.WriteLine(string.Join(',', res.labels));
            return;

            //Console.WriteLine(string.Join(",", id));

            //return;

            //var cb = new CompareBenchmark();

            //cb.Setup();
            //IList<int> ids;
            //string text = cb.TextSamples().Last();
            //int i = 0;
            //string res;
            ////   while (i < 100000)
            //{
            //    i++;
            //    ids = cb._sharpToken.Encode(text);
            //    Console.WriteLine(string.Join(",", ids));
            //    res = cb._sharpToken.Decode(ids);
            //    Console.WriteLine(res);


            //    ids = cb._tikToken.Encode(text);
            //    Console.WriteLine(string.Join(",", ids));
            //    res = cb._tikToken.Decode(ids.ToList());
            //    Console.WriteLine(res);

            //    ids = cb._tokenizer1.Encode(text, false);
            //    Console.WriteLine(string.Join(",", ids));
            //    res = cb._tokenizer1.Decode(ids.ToArray());
            //    Console.WriteLine(res);

            //    ids = cb._tokenizer2.Encode(text);
            //    Console.WriteLine(string.Join(",", ids));
            //    res = cb._tokenizer2.Decode(ids.ToArray());
            //    Console.WriteLine(res);
            //}

            //Console.WriteLine();
            //Console.WriteLine("All done");
            //Console.ReadLine();
            //return;

            BenchmarkRunner.Run<CompareBenchmark>();

        }
    }


    [MemoryDiagnoser]
    public class CompareBenchmark
    {
        internal GptEncoding _sharpToken;
        internal TikToken _tikToken;
        internal BPETokenizer _tokenizer1;
        internal ConcurrentBPETokenizer _tokenizer2;

        [GlobalSetup]
        public void Setup()
        {
            _sharpToken = GptEncoding.GetEncoding("cl100k_base");
            _tikToken = TikToken.GetEncodingAsync("cl100k_base").ConfigureAwait(false).GetAwaiter().GetResult();
            _tokenizer1 = BPETokenizer.CreateTokenizer(
                @"D:\Data\Personal\AI\llm\tokenizer\cl100k.txt", true);
            _tokenizer2 = ConcurrentBPETokenizer.CreateTokenizer(
                @"D:\Data\Personal\AI\llm\tokenizer\cl100k.txt", true);
        }

        // ====== 1. 声明参数源 ======
        public IEnumerable<string> TextSamples()
        {
            yield return TextCatalog.English;
            yield return TextCatalog.Chinese;
            yield return TextCatalog.Mixed;
        }

        // ====== 2. 每个方法带参数 ======
        [Benchmark]
        [ArgumentsSource(nameof(TextSamples))]
        public int SharpToken_cl100k_base(string text)
        {
            var encoded = _sharpToken.Encode(text);
            var decoded = _sharpToken.Decode(encoded);
            return encoded.Count;
        }

        [Benchmark]
        [ArgumentsSource(nameof(TextSamples))]
        public int TiktokenSharp_cl100k_base(string text)
        {
            var encoded = _tikToken.Encode(text);
            var decoded = _tikToken.Decode(encoded);
            return encoded.Count;
        }

        [Benchmark(Baseline =true)]
        [ArgumentsSource(nameof(TextSamples))]
        public int LumTokenizer_cl100k_base(string text)
        {
            var encoded = _tokenizer1.Encode(text, false);
            var decoded = _tokenizer1.Decode(encoded, false);
            return encoded.Count;
        }

        [Benchmark]
        [ArgumentsSource(nameof(TextSamples))]
        public int LumTokenizer_concurrent_cl100k_base(string text)
        {
            var encoded = _tokenizer2.Encode(text, false);
            var decoded = _tokenizer2.Decode(encoded, false);
            return encoded.Count;
        }
    }
    public static class TextCatalog
    {
        /* 1 英文长对话 */
        public static readonly string English =
            "Human: Can you explain how gradient descent works in deep learning?\n\n" +
            "Assistant: Sure! Gradient descent is an optimization algorithm used to minimize the loss function. " +
            "The basic idea is to compute the gradient of the loss with respect to each parameter, then update " +
            "the parameters in the opposite direction of the gradient. The learning rate controls the step size. " +
            "There are variants like SGD, momentum, Adam, each improving convergence speed or stability. " +
            "In practice, we use mini-batch gradient descent to balance computational efficiency and convergence. " +
            "The loss landscape can be very high-dimensional and non-convex, so careful tuning of hyper-parameters " +
            "such as learning rate schedules, weight decay, and initialization strategies is essential. " +
            "Without these tricks, training can stall or diverge.\n\n" +
            "Human: What are the common tricks to avoid overfitting?\n\n" +
            "Assistant: Common regularization techniques include dropout, weight decay (L2), early stopping, " +
            "data augmentation, and batch normalization. Increasing dataset size and using simpler models also help.";

        /* 2 纯中文长对话 */
        public static readonly string Chinese =
            "人类：请详细介绍一下 Transformer 的核心思想。\n\n" +
            "助手：Transformer 完全摒弃了递归结构，仅依靠自注意力机制来捕捉序列中的长距离依赖。 " +
            "输入序列首先被映射为查询、键和值三个向量，接着通过缩放点积注意力计算每一位置对其他位置的权重， " +
            "从而在一次前向传播中同时聚合全局信息。多头机制允许模型在不同子空间内并行学习多种关系。 " +
            "此外，位置编码被直接加到词向量上，为模型提供顺序信息。整体结构由编码器和解码器堆叠而成， " +
            "每一层都包含多头自注意力、前馈网络、残差连接和层归一化。该设计大幅提升了训练并行度， " +
            "成为后续 BERT、GPT 系列以及 T5 等模型的基础，推动了预训练加微调的新范式。\n\n" +
            "人类：它与传统 RNN 相比有什么优势？\n\n" +
            "助手：最主要的优势是并行化。RNN 必须依次计算隐藏状态，而 Transformer 可一次性处理整个序列， " +
            "训练速度显著提高。同时，自注意力直接建模任意两位置间的依赖，缓解了长距离梯度消失问题。";

        /* 3 中英混合长对话（无 special token） */
        public static readonly string Mixed =
            "User：最近大模型很火，能不能用简单 English 解释一下 RLHF 是怎么做的？\n\n" +
            "Assistant：RLHF 全称 Reinforcement Learning from Human Feedback，核心流程分三步。 " +
            "第一步，用 supervised fine-tuning 在高质量人工标注数据上微调 base 模型，得到 SFT 模型。 " +
            "第二步，收集同一 prompt 下多个 response 的对比数据，训练一个 reward model 来打分。 " +
            "第三步，用 reinforcement learning（通常是 PPO）继续优化 SFT 模型，把 reward model 的分数作为 reward signal， " +
            "同时加入 KL penalty 防止模型偏离原始分布太远。迭代几轮后，模型就能输出更对齐人类偏好的答案。 " +
            "整个 pipeline 需要大量人工标注和计算资源，但效果上能显著降低 harmful 或 untruthful 输出的概率。\n\n" +
            "User：训练 reward model 时有哪些 tricks？\n\n" +
            "Assistant：常见技巧包括 pair-wise 排序损失、对同一 batch 内样本做 normalization、 " +
            "以及使用 larger batch size 和 lower learning rate 来稳定训练。数据质量比数量更重要， " +
            "需要严格过滤 inconsistent 或恶意标注的样本。";
    }


}
