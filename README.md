# LumTokenizer 1.0.7

**English:** A BPE tokenizer for .NET that loads **Hugging Face `tokenizer.json`** (vocab, merges, `added_tokens`, `normalizer`, `pre_tokenizer`) and runs a compiled encode-time pipeline aligned with upstream behavior, with **DeepSeek V4–style configs** as the primary interoperability target.

面向 .NET（当前目标框架 .NET 10）的 BPE 分词库：从 **`tokenizer.json`** 加载词表与合并规则，并在编码路径上执行与上游尽量一致的 **normalizer** 与 **pre_tokenizer** 管线。

---

## 快速开始

### 安装

```bash
dotnet add package LumTokenizer
```

### 编码与解码

```csharp
using LumTokenizer.Tokenizer;

// 从 tokenizer.json 加载（默认按 merges 是否为「字符串行」自动选择反序列化分支）
var tokenizer = BPETokenizer.CreateTokenizer("path/to/tokenizer.json", mergesAsString: false);
var ctokenizer = ConcurrentBPETokenizer.CreateTokenizer("path/to/tokenizer.json", mergesAsString: false);

Console.WriteLine(tokenizer.VocabSize);

var ids = tokenizer.Encode("hello!  你好");
Console.WriteLine(string.Join(",", ids));
Console.WriteLine(tokenizer.Decode(ids));

// 若 merges 为 cl100k 一类「每行一个 pair 字符串」格式，可显式使用：
// BPETokenizer.CreateTokenizer("cl100k.txt", mergesAsString: true);
```

- **`BPETokenizer`**：单线程使用。
- **`ConcurrentBPETokenizer`**：多线程共享实例时使用。

---

## 为何以 DeepSeek V4 的 `tokenizer.json` 为适配基准

DeepSeek V4（及同族）导出的 `tokenizer.json` 在真实业务里同时具备：

- **`model.merges` 为字符串数组**（`"a b"` 一行一个 pair），而非仅二维 `["a","b"]`；
- **`pre_tokenizer` 常为 `Sequence`**，内含 **多段 `Split`（正则 / String 字面量等多种 JSON 形态）** 再接 **ByteLevel**；
- **`added_tokens`** 与对话模板中的特殊串强相关。

我们以 **该类配置文件能正确加载、且编码结果与 Hugging Face `tokenizers` / 官方推理栈一致** 为第一目标；其它模型的 `tokenizer.json` 只要在下列字段能力范围内，一般也可直接使用。若某模型超出下表（例如 `normalizer: Precompiled`），会在构造或编译管线时显式报错或标注未支持。

---

## 设计取舍（我们是怎么考虑的）

1. **语义优先于「手写 RegexType」**  
   早期可在代码里固定一套正则做预切分；一旦对接任意 `tokenizer.json`，切分规则必须以 JSON 里的 **`pre_tokenizer`** 为准。因此编码前增加 **由 JSON 编译出的管线**（`TokenizerEncodePipeline`）：构造完成后根据 `normalizer` / `pre_tokenizer` 生成委托与步骤数组，避免在热路径上反复解析 JSON。

2. **ByteLevel 与 BPE 的衔接**  
   当 `pre_tokenizer` 链 **以 ByteLevel 结尾** 时，片段已是词表所用字符空间，实现上会 **跳过再次 UTF-8 + byte 映射**，直接与 BPE 表交互，避免双重映射错误。

3. **`merges` 双格式**  
   HuggingFace 常见为字符串行；部分工具导出为二维数组。`TokMap` 会检测 `model.merges` 首元素类型并走对应反序列化，再统一成内部 BPE 所需的「空格分隔 pair」行格式。

4. **性能与 AOT**  
   BPE 核心仍配合 **`ArrayPool`** 等习惯用法；编码前管线工作在 **`string` 片段列表**上，与「纯 Span、零配置」相比会增加分配与分支，这是为兼容真实 `tokenizer.json` 的必要成本。  
   类库仍标记 **`IsAotCompatible`**；运行时编码路径不依赖反射。加载 JSON 仍使用 `System.Text.Json` 的反射式反序列化，在 **裁剪 / Native AOT** 宿主中需按 .NET 文档自行处理 trim 或源生成上下文（与多数类库一致）。

5. **与上游的边界**  
   极端 `Split` 行为、`Metaspace.prepend_scheme`、`UnicodeScripts` 等与 Rust 版逐字节一致性的差异可能存在；遇到与官方不一致时，建议以 **同一段文本 + 同一份 `tokenizer.json`** 对照 HF 输出再提 issue。

---

## `tokenizer.json` 字段与能力（概览）

下列为 **当前版本会读取或参与编码语义** 的节点；未列出的字段（如 **`decoder`**、**`post_processor`**、**`model` 非 BPE** 等）**未接入** 本库的加载与编码逻辑。

### `model`

| 字段 | 说明 |
|------|------|
| **`vocab`** | 词表 `token → id`，必需。 |
| **`merges`** | 支持 **`["a b", ...]`**（字符串行）与 **`[["a","b"], ...]`**（二维数组）；前者可通过 `TokMap.LoadFromTokenizerJson` 自动识别，或统一使用 `CreateTokenizer(..., mergesAsString: true/false)` 与对应加载 API。 |

### `added_tokens`

| 字段 | 说明 |
|------|------|
| **`id` / `content`** | 特殊 token；编码时优先按最长匹配拆分，整段命中则走特殊 id。 |

### `normalizer`

支持 **`type: "Sequence"`** 嵌套，以及下列子类型（未列出即构造编译阶段可能 **`NotSupportedException`**）：

| `type` | 说明 |
|--------|------|
| **Sequence** | 子链 `normalizers`。 |
| **Lowercase** | `ToLowerInvariant()`。 |
| **NFC / NFD / NFKC / NFKD** | Unicode 规范化形式。 |
| **Strip** | `left` / `right` 控制 `TrimStart` / `TrimEnd`。 |
| **Replace** | `pattern`（与 Split 相同的多形态 JSON）+ `content`。 |
| **StripAccents** | 去重音类组合字符。 |
| **ByteLevel** | 对 UTF-8 字节做 ByteLevel 字符映射（与 HF 语义对齐为主）。 |
| **Nmt** | 控制字符与空白归一化近似。 |
| **BertNormalizer** | `clean_text`、`handle_chinese_chars`、`strip_accents`、`lowercase`。 |
| **Precompiled** | **不支持**（需 SentencePiece 等预编译表）。 |

### `pre_tokenizer`

支持 **`type: "Sequence"`** 与下列子类型：

| `type` | 主要 JSON 字段 | 说明 |
|--------|----------------|------|
| **Sequence** | `pretokenizers` | 顺序执行子步骤。 |
| **Split** | `pattern`（字符串 / `Regex` / `String` 对象）、`behavior`、`invert` | `behavior` 支持常见写法：`Isolated`、`Removed`、`MergedWithPrevious`、`MergedWithNext`、`Contiguous`（下划线/连写均可）。 |
| **ByteLevel** | `add_prefix_space`、`use_regex` | `use_regex` 为 false 时整段映射；GPT 式切分使用内置正则。字段 **`trim_offsets`** 可出现在 JSON 中，**当前编码实现未单独实现其偏移语义**。 |
| **Whitespace** / **WhitespaceSplit** | — | 空白处理。 |
| **BertPreTokenizer** | — | 空白与标点切分近似。 |
| **Digits** | `individual_digits` | 数字是否逐位拆开。 |
| **Punctuation** | `behavior` | 与 Split 类似的 `Isolated` 等策略。 |
| **Metaspace** | `replacement`（**单字符**）、`prepend_scheme` | `prepend_scheme`：`always` / `first` / `never` 等常见取值。 |
| **CharDelimiterSplit** | `delimiter`（**单字符**） | 按分隔符切开。 |
| **Replace** | `pattern`、`content` | 正则替换。 |
| **Strip** | `lstrip`、`rstrip` | 段首尾空白。 |
| **UnicodeScripts** | — | **按脚本粗分桶的简化实现**，与上游「按 Unicode Script 属性切分」在边界上可能略有差异。 |

---

## 测试与贡献

- 仓库内 **`UnitTest`** 覆盖基础与鲁棒性用例；对接新模型时建议用 **HF `tokenizers` 或 `transformers`** 对同一输入做 id 对比。
- 欢迎通过 Issue / PR 反馈未覆盖的 `tokenizer.json` 片段或边界行为。

## 许可证

LumTokenizer 使用 **MIT** 许可证，见 [LICENSE](LICENSE.txt)。
