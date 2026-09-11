using Erp.Infrastructure.AI;

namespace Erp.Infrastructure.Tests;

/// system prompt 的規則覆蓋檢查。
///
/// 這組測試**不驗證 LLM 是否真的遵守**這些規則 —— 那需要真實 API 呼叫與行為評測。
/// 它防的是另一件事：有人重寫或精簡 prompt 時，把某條規則整個刪掉而沒人發現。
/// 每一條都對應規劃文件裡明確要求的行為。
public class SystemPromptTests
{
    private static readonly string Prompt = AiSystemPrompt.Text;

    [Theory]
    [InlineData("ENTITY_NOT_FOUND", "查無資料的錯誤碼處理")]
    [InlineData("INVALID_ARGUMENT", "參數錯誤的錯誤碼處理")]
    [InlineData("NOT_APPLICABLE", "問法不適用的錯誤碼處理")]
    [InlineData("INTERNAL_ERROR", "系統錯誤的錯誤碼處理")]
    [InlineData("SERVICE_UNAVAILABLE", "外部服務不可用的錯誤碼處理")]
    [InlineData("search_documents", "文件語意檢索的使用時機")]
    [InlineData("available_qty", "可用庫存優先於帳上庫存")]
    [InlineData("search_items", "多筆結果要請使用者確認")]
    [InlineData("suggested_order_qty", "採購建議直接引用後端數字")]
    [InlineData("check_material_sufficiency_for_item", "夠不夠做要用專用工具")]
    public void 關鍵規則不得從系統提示詞中消失(string keyword, string rule)
        => Assert.True(Prompt.Contains(keyword), $"system prompt 少了「{rule}」相關的規則（缺少 {keyword}）");

    [Fact]
    public void 必須明文禁止自行推算數字()
    {
        Assert.Contains("禁止自行推算", Prompt);
        Assert.Contains("不可以編造", Prompt);
    }

    [Fact]
    public void 必須說明只能查詢不能異動資料()
    {
        Assert.Contains("沒有異動資料的權限", Prompt);
    }

    [Fact]
    public void 必須規定超出範圍的問題要禮貌拒答()
    {
        Assert.Contains("職責範圍", Prompt);
        Assert.Contains("不要勉強回答", Prompt);
    }

    [Fact]
    public void 必須規定同樣參數失敗過就不要重試()
    {
        Assert.Contains("相同參數", Prompt);
        Assert.Contains("不要再用相同參數呼叫第二次", Prompt);
    }

    [Fact]
    public void 必須禁止編造文件來源()
    {
        // 等價於「所有數字由後端算好」：引用座標只能來自工具回傳值。
        // 少了這條，LLM 可以說出一個聽起來很像公司 SOP 的檔名
        Assert.Contains("source_name", Prompt);
        Assert.Contains("禁止自行改寫", Prompt);
    }

    [Fact]
    public void 必須規定文件查不到時不可改用自己的知識回答()
    {
        Assert.Contains("chunks 為空", Prompt);
        Assert.Contains("不可以改用你自己的知識回答", Prompt);
    }

    [Fact]
    public void 必須禁止把相似度當成百分比轉述()
    {
        Assert.Contains("similarity", Prompt);
        Assert.Contains("不要當成百分比", Prompt);
    }

    [Fact]
    public void 必須指定以繁體中文回答()
    {
        Assert.Contains("繁體中文", Prompt);
    }
}
