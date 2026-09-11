namespace Erp.Infrastructure.AI;

/// 工具失敗的分類。
///
/// LLM 只讀中文訊息的話無法穩定判斷該怎麼反應 ——
/// 「查無這筆資料」該告訴使用者查不到，「參數不對」該換個參數重試一次，
/// 「系統錯誤」則不該重試。錯誤碼讓這個判斷有依據。
public enum ToolErrorCode
{
    /// 查無這筆資料。資料本身不存在，不是系統故障
    EntityNotFound,

    /// 參數缺漏或格式不對。改正參數後重試一次是合理的
    InvalidArgument,

    /// 參數合法但這個問法對這筆資料不適用（例如對原物料問可製造量）
    NotApplicable,

    /// 呼叫了不存在的工具
    UnknownTool,

    /// 未預期的系統錯誤。重試沒有意義
    InternalError,

    /// 工具依賴的外部服務不可用（例如文件檢索需要的本機 Ollama 沒有啟動）。
    ///
    /// 與 InternalError 分開是有意義的：這不是程式壞了，而是環境少裝了一個可選元件。
    /// 合併成 InternalError 的話，LLM 只能說「查詢失敗」，
    /// 而這其實是最常見、也最該給出明確指引的情境。
    ServiceUnavailable
}

public static class ToolErrorCodeExtensions
{
    /// 對外的錯誤碼字串是契約的一部分，寫死在這裡，
    /// 這樣改 enum 成員名稱不會意外改掉送給 LLM 的值
    public static string ToWireValue(this ToolErrorCode code) => code switch
    {
        ToolErrorCode.EntityNotFound => "ENTITY_NOT_FOUND",
        ToolErrorCode.InvalidArgument => "INVALID_ARGUMENT",
        ToolErrorCode.NotApplicable => "NOT_APPLICABLE",
        ToolErrorCode.UnknownTool => "UNKNOWN_TOOL",
        ToolErrorCode.InternalError => "INTERNAL_ERROR",
        ToolErrorCode.ServiceUnavailable => "SERVICE_UNAVAILABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "未定義的錯誤碼")
    };
}
