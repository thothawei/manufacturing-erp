namespace Erp.Infrastructure.Rag;

/// embedding 服務不可用：沒裝 Ollama、沒啟動、模型沒 pull、回非 2xx、逾時。
///
/// 與 LlmUnavailableException 同一個形狀 —— 外部服務不可用是可預期的情境，
/// 不該以未預期例外的形式穿過 tool-use 迴圈炸掉整段對話。
public sealed class EmbeddingUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
