using Erp.Application.Ml;

// 產生訓練用的模擬資料 CSV。
//
// 為什麼資料生成寫在 C# 而不是訓練腳本裡：特徵定義必須只有一份。
// 由 Python 生成的話，特徵/需求量的計算會有兩個實作 —— 一個在訓練腳本、一個在線上推論 ——
// 而兩邊差一點點就是 training/serving skew，模型上線後表現與離線評估對不上，
// 還很難查。這裡輸出的欄位順序直接來自對應的特徵定義型別。
//
// 用法：
//   dotnet run --project tools/Erp.MlDataGen                                       # 預設：工單延遲風險資料
//   dotnet run --project tools/Erp.MlDataGen -- [輸出路徑] [筆數] [亂數種子]        # 同上，可覆寫參數
//   dotnet run --project tools/Erp.MlDataGen -- demand-forecast [輸出路徑] [週數] [亂數種子]  # S6：物料需求歷史

var knownGenerators = new[] { "work-order-history", "demand-forecast" };
var hasExplicitGenerator = args.Length > 0 && knownGenerators.Contains(args[0]);
var generator = hasExplicitGenerator ? args[0] : "work-order-history";
var rest = hasExplicitGenerator ? args[1..] : args;

if (generator == "demand-forecast")
{
    GenerateDemandForecastData(rest);
}
else
{
    GenerateWorkOrderHistoryData(rest);
}

static void GenerateWorkOrderHistoryData(string[] args)
{
    var outputPath = args.Length > 0 ? args[0] : "ml/data/work-order-history.csv";
    var count = args.Length > 1 ? int.Parse(args[1]) : 1200;
    var seed = args.Length > 2 ? int.Parse(args[2]) : 20260913;

    var records = new HistoricalWorkOrderGenerator(seed).Generate(count);

    EnsureDirectoryExists(outputPath);
    File.WriteAllText(outputPath, HistoricalWorkOrderGenerator.ToCsv(records));

    var delayed = records.Count(r => r.WasDelayed);
    var missingReadiness = records.Count(r => r.MaterialReadiness is null);
    var missingLoad = records.Count(r => r.WeeklyLoadRatio is null);

    Console.WriteLine($"已寫出 {records.Count} 筆到 {outputPath}（seed={seed}）");
    Console.WriteLine($"  延遲比例：{delayed}/{records.Count} = {delayed / (double)records.Count:P1}");
    Console.WriteLine($"  齊套率缺值：{missingReadiness} 筆");
    Console.WriteLine($"  當週負載缺值：{missingLoad} 筆");
}

static void GenerateDemandForecastData(string[] args)
{
    var outputPath = args.Length > 0 ? args[0] : "ml/data/material-demand-history.csv";
    var weeks = args.Length > 1 ? int.Parse(args[1]) : 156;
    var seed = args.Length > 2 ? int.Parse(args[2]) : 20260917;

    var records = new MaterialDemandHistoryGenerator(seed).Generate(weeks);

    EnsureDirectoryExists(outputPath);
    File.WriteAllText(outputPath, MaterialDemandHistoryGenerator.ToCsv(records));

    var byItem = records.GroupBy(r => r.ItemCode);
    var missing = records.Count(r => r.DemandQty is null);

    Console.WriteLine($"已寫出 {records.Count} 筆到 {outputPath}（seed={seed}，每個品項 {weeks} 週）");
    foreach (var group in byItem)
    {
        var values = group.Where(r => r.DemandQty is not null).Select(r => r.DemandQty!.Value).ToList();
        Console.WriteLine($"  {group.Key}：平均 {values.Average():F1}，範圍 {values.Min():F0}~{values.Max():F0}");
    }
    Console.WriteLine($"  缺值：{missing} 筆");
}

static void EnsureDirectoryExists(string outputPath)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
}
