using System.Text;

namespace Erp.Infrastructure.Rag;

/// 把一份文件切成可檢索的片段。
///
/// 策略：依段落切、不重疊（docs/rag-module-plan-v1.md 決策 D2-A）。
/// 不重疊讓 chunk_index 與原文段落一對一，引用座標是精確的；
/// 代價是跨段落的答案會被切斷（判定標準在第 3 段、處置方式在第 4 段時，
/// 只命中一段會答不完整）。語料撰寫時要讓「一個主題一段」成立來補這個缺。
public sealed class DocumentChunker(int maxChars = 400, int minChars = 40)
{
    public IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var paragraphs = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var chunks = new List<string>();
        foreach (var paragraph in MergeShortParagraphs(paragraphs))
        {
            chunks.AddRange(paragraph.Length <= maxChars ? [paragraph] : SplitLongParagraph(paragraph));
        }

        return chunks;
    }

    /// 標題行單獨成段會變成一個幾乎沒有資訊的片段，檢索時只會佔掉 top_k 的位置。
    /// 太短的段落併進下一段，讓標題跟著它標的內容走。
    private List<string> MergeShortParagraphs(List<string> paragraphs)
    {
        var merged = new List<string>();
        var pending = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            if (pending.Length > 0)
            {
                pending.Append('\n');
            }
            pending.Append(paragraph);

            if (pending.Length >= minChars)
            {
                merged.Add(pending.ToString());
                pending.Clear();
            }
        }

        // 最後一段還不夠長時併進前一段，而不是留一個零碎片段
        if (pending.Length > 0)
        {
            if (merged.Count > 0)
            {
                merged[^1] = merged[^1] + "\n" + pending;
            }
            else
            {
                merged.Add(pending.ToString());
            }
        }

        return merged;
    }

    /// 過長的段落按句末標點切，貪婪塞到 maxChars。
    /// 單一句子就超過 maxChars 時硬切 —— 寧可切在奇怪的位置，也不要產生一個超大片段，
    /// 那會讓相似度被稀釋（一個片段講五件事，對其中任一件的分數都不高）。
    private List<string> SplitLongParagraph(string paragraph)
    {
        var result = new List<string>();
        var current = new StringBuilder();

        foreach (var sentence in SplitSentences(paragraph))
        {
            if (current.Length > 0 && current.Length + sentence.Length > maxChars)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }

            if (sentence.Length > maxChars)
            {
                for (var offset = 0; offset < sentence.Length; offset += maxChars)
                {
                    result.Add(sentence.Substring(offset, Math.Min(maxChars, sentence.Length - offset)).Trim());
                }
                continue;
            }

            current.Append(sentence);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString().Trim());
        }

        return [.. result.Where(s => s.Length > 0)];
    }

    /// 句末標點後切，標點留在句尾。中文沒有空格可依靠，只能靠標點。
    private static IEnumerable<string> SplitSentences(string paragraph)
    {
        var start = 0;
        for (var i = 0; i < paragraph.Length; i++)
        {
            if (paragraph[i] is '。' or '！' or '？' or '\n' or '；')
            {
                yield return paragraph[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < paragraph.Length)
        {
            yield return paragraph[start..];
        }
    }
}
