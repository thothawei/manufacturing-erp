namespace Erp.Infrastructure.Tests;

/// 把一組模型檔（.onnx + .json）複製進一個獨立的臨時目錄，給需要「模型檔不存在」
/// 或「metadata 跟程式碼對不上」這類情境的測試用。
///
/// 為什麼不直接動 AppContext.BaseDirectory 底下那份共用檔案：那份檔案在 xUnit 預設的
/// 平行測試下，同組件裡其他測試類別（凡是會建立真的 OnnxDelayRiskModel／
/// OnnxMaterialDemandForecastModel 的）隨時可能同時在讀，直接搬走或改寫會是一個
/// 間歇性、跟被測程式碼無關的假紅燈。複製到獨立目錄之後，每個測試都在自己的沙盒裡動，
/// 不會影響任何別的測試。
internal sealed class TempModelDirectory : IDisposable
{
    public string Directory { get; }

    public string MetadataPath { get; }

    public TempModelDirectory(string sourceDirectory, string modelFileStem, bool copyOnnx = true)
    {
        Directory = Path.Combine(Path.GetTempPath(), $"erp-ml-test-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);

        MetadataPath = Path.Combine(Directory, $"{modelFileStem}.json");
        File.Copy(Path.Combine(sourceDirectory, $"{modelFileStem}.json"), MetadataPath);

        if (copyOnnx)
        {
            File.Copy(
                Path.Combine(sourceDirectory, $"{modelFileStem}.onnx"),
                Path.Combine(Directory, $"{modelFileStem}.onnx"));
        }
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // 清不掉臨時目錄不該讓測試本身失敗——它跑在系統暫存區，之後總會被清掉
        }
    }
}
