using Erp.Application.Ml;

// 產生訓練用的模擬歷史工單 CSV。
//
// 為什麼資料生成寫在 C# 而不是訓練腳本裡：特徵定義必須只有一份。
// 由 Python 生成的話，「齊套率」會有兩個實作 —— 一個在訓練腳本、一個在線上推論 ——
// 而兩邊差一點點就是 training/serving skew，模型上線後表現與離線評估對不上，
// 還很難查。這裡輸出的欄位順序直接來自 WorkOrderDelayFeatures.FeatureNames。
//
// 用法：dotnet run --project tools/Erp.MlDataGen -- [輸出路徑] [筆數] [亂數種子]

var outputPath = args.Length > 0 ? args[0] : "ml/data/work-order-history.csv";
var count = args.Length > 1 ? int.Parse(args[1]) : 1200;
var seed = args.Length > 2 ? int.Parse(args[2]) : 20260913;

var records = new HistoricalWorkOrderGenerator(seed).Generate(count);

var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
if (!string.IsNullOrEmpty(directory))
{
    Directory.CreateDirectory(directory);
}

await File.WriteAllTextAsync(outputPath, HistoricalWorkOrderGenerator.ToCsv(records));

var delayed = records.Count(r => r.WasDelayed);
var missingReadiness = records.Count(r => r.MaterialReadiness is null);
var missingLoad = records.Count(r => r.WeeklyLoadRatio is null);

Console.WriteLine($"已寫出 {records.Count} 筆到 {outputPath}（seed={seed}）");
Console.WriteLine($"  延遲比例：{delayed}/{records.Count} = {delayed / (double)records.Count:P1}");
Console.WriteLine($"  齊套率缺值：{missingReadiness} 筆");
Console.WriteLine($"  當週負載缺值：{missingLoad} 筆");
