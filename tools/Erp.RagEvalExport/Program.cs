using System.Text.Json;
using Erp.Infrastructure.Rag;

// 把 RAG 的語料切段結果與標註評測集匯出成 jsonl，供 ml/rerank_eval.py 離線量測用。
//
// 切段邏輯只有 C# 這一份（DocumentChunker、DemoCorpus），Python 不重新實作一次 ——
// 理由與 ml/train.py 開頭寫的一樣：兩份實作只要有一點點差異，
// 就是拿著不同的候選集在比較兩階段檢索的效果，離線量出來的數字對不到線上。
// Python 只負責「拿到片段之後怎麼評分、怎麼排序」。
//
// 用法：dotnet run --project tools/Erp.RagEvalExport -- [輸出目錄]

var outputDir = args.Length > 0 ? args[0] : "ml/data";
Directory.CreateDirectory(outputDir);

var jsonOptions = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

var chunker = new DocumentChunker();
var chunkPath = Path.Combine(outputDir, "rag-chunks.jsonl");
using (var writer = new StreamWriter(chunkPath, append: false))
{
    var chunkCount = 0;
    foreach (var document in DemoCorpus.All)
    {
        var pieces = chunker.Split(document.Text);
        for (var index = 0; index < pieces.Count; index++)
        {
            var record = new { text = pieces[index], sourceName = document.SourceName, chunkIndex = index };
            writer.WriteLine(JsonSerializer.Serialize(record, jsonOptions));
            chunkCount++;
        }
    }

    Console.WriteLine($"已寫出 {chunkCount} 個片段（來自 {DemoCorpus.All.Count} 份文件）到 {chunkPath}");
}

var queryPath = Path.Combine(outputDir, "rag-queries.jsonl");
using (var writer = new StreamWriter(queryPath, append: false))
{
    foreach (var q in RetrievalEvaluationSet.All)
    {
        var record = new
        {
            query = q.Query,
            kind = q.Kind.ToString(),
            expectedSource = q.ExpectedSource,
            alternativeSource = q.AlternativeSource
        };
        writer.WriteLine(JsonSerializer.Serialize(record, jsonOptions));
    }

    Console.WriteLine($"已寫出 {RetrievalEvaluationSet.All.Count} 組標註查詢到 {queryPath}");
}
