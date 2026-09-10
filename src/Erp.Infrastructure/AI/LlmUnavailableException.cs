namespace Erp.Infrastructure.AI;

/// LLM 服務無法使用（未設定金鑰、限流、對方故障等）。
/// 供應商專屬的例外在 AnthropicLlmClient 就被轉成這個型別，
/// 上層不必認識任何一家 LLM 廠商的例外類別，也不會把 SDK 堆疊洩漏給使用者。
public sealed class LlmUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
