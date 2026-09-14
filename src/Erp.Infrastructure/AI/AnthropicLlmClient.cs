using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.AI;

/// ILlmClient 的 Anthropic 實作。
/// 只做兩件事：把中性模型轉成 SDK 型別送出，再把回應轉回中性模型。
/// 換成別家 LLM 時只要換掉這個類別，tool-use 迴圈與工具定義都不用動。
/// 端點可以是 Anthropic 官方，也可以是 OmniRoute 這類 Anthropic 相容 gateway
/// —— 兩者的差別只在設定，見 AiAssistantOptions 的 BaseUrl 與 UseServerSideFallback。
public sealed class AnthropicLlmClient : ILlmClient
{
    /// 伺服器端 refusal fallback：安全分類器拒絕時自動改由備援模型作答，
    /// 使用者不會收到一個沒有內容的失敗回應。
    private const string ServerSideFallbackBeta = "server-side-fallback-2026-06-01";
    private const string FallbackModel = "claude-opus-4-8";

    /// OmniRoute 在上游回空內容時會補一個寫死這句話的 text 區塊
    /// （open-sse/handlers/responseTranslator.ts，沒有開關可以關掉）。
    /// 官方端點不會有這一塊，留著它有兩個後果：它會被當成 assistant 的發言
    /// 回送進對話歷史，而且「上游沒給內容」會變成一句使用者看不懂的英文佔位符。
    /// 實測確認過的行為，見 README「AI 助理」。
    private const string GatewayEmptyPlaceholder = "(empty response)";

    private readonly AnthropicClient _client;
    private readonly AiAssistantOptions _options;

    public AnthropicLlmClient(IOptions<AiAssistantOptions> options)
    {
        _options = options.Value;
        // ApiKey 與 BaseUrl 都是 init-only，只能在物件初始設定式裡指定；
        // 沒設定的項目要整個省略，設成 null 會蓋掉 SDK 自己的預設解析
        var hasApiKey = !string.IsNullOrWhiteSpace(_options.ApiKey);
        var hasBaseUrl = !string.IsNullOrWhiteSpace(_options.BaseUrl);

        var httpClient = new HttpClient(new UnicodeNormalizingHandler())
        {
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };

        // hasApiKey / hasBaseUrl 已經確認過非空，用 ! 告訴編譯器這件事
        _client = (hasApiKey, hasBaseUrl) switch
        {
            (true, true) => new AnthropicClient
            { ApiKey = _options.ApiKey!, BaseUrl = _options.BaseUrl!, HttpClient = httpClient },
            (true, false) => new AnthropicClient { ApiKey = _options.ApiKey!, HttpClient = httpClient },
            (false, true) => new AnthropicClient { BaseUrl = _options.BaseUrl!, HttpClient = httpClient },
            _ => new AnthropicClient { HttpClient = httpClient }  // 由 SDK 讀 ANTHROPIC_API_KEY
        };
    }

    public async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken ct = default)
    {
        try
        {
            return await SendCoreAsync(request, ct);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new LlmUnavailableException("AI 助理尚未設定 API 金鑰，或金鑰無效", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new LlmUnavailableException("AI 服務目前流量過高，請稍後再試", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new LlmUnavailableException("AI 服務暫時無法回應，請稍後再試", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new LlmUnavailableException("無法連線到 AI 服務，請檢查網路連線", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient 逾時會以 TaskCanceledException 呈現；
            // 呼叫端主動取消時 ct 會是 cancelled，那種情況要原樣往上拋
            throw new LlmUnavailableException(
                $"AI 服務在 {_options.TimeoutSeconds} 秒內沒有回應", ex);
        }
        catch (AnthropicApiException ex)
        {
            // 其餘 API 錯誤（參數不合法等）一律不把 SDK 細節往上拋
            throw new LlmUnavailableException("呼叫 AI 服務失敗", ex);
        }
    }

    private async Task<LlmResponse> SendCoreAsync(LlmRequest request, CancellationToken ct)
    {
        var response = await _client.Beta.Messages.Create(BuildParams(request));

        return new LlmResponse([.. response.Content.Select(ToNeutralBlock).OfType<LlmContentBlock>()]);
    }

    /// refusal fallback 只有 Anthropic 官方端點吃得下，打 gateway 時整組省略。
    /// 理由跟建構式那邊一樣：不要的項目要整個不出現在初始設定式裡 ——
    /// 設成 null 不是「沒送」，SDK 會照樣把 "fallbacks": null 寫進請求本文。
    private MessageCreateParams BuildParams(LlmRequest request)
    {
        List<BetaToolUnion> tools = [.. request.Tools.Select(ToSdkTool)];
        List<BetaMessageParam> messages = [.. request.Messages.Select(ToSdkMessage)];

        if (!_options.UseServerSideFallback)
        {
            return new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                System = request.SystemPrompt,
                Tools = tools,
                Messages = messages
            };
        }

        return new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            System = request.SystemPrompt,
            Betas = [ServerSideFallbackBeta],
            Fallbacks = new BetaFallbacksParam(new BetaFallbackParam[] { new() { Model = FallbackModel } }),
            Tools = tools,
            Messages = messages
        };
    }

    private static BetaToolUnion ToSdkTool(ToolDefinition tool) => new BetaTool
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = new()
        {
            Properties = tool.Properties.ToDictionary(p => p.Key, p => p.Value),
            Required = [.. tool.Required]
        }
    };

    private static BetaMessageParam ToSdkMessage(LlmMessage message) => new()
    {
        Role = message.Role == LlmRole.User ? Role.User : Role.Assistant,
        Content = message.Content.Select(ToSdkBlock).ToList()
    };

    private static BetaContentBlockParam ToSdkBlock(LlmContentBlock block) => block switch
    {
        LlmTextBlock text => new BetaTextBlockParam { Text = text.Text },

        LlmToolUseBlock toolUse => new BetaToolUseBlockParam
        {
            ID = toolUse.ToolUseId,
            Name = toolUse.ToolName,
            Input = ToInputDictionary(toolUse.Arguments)
        },

        LlmToolResultBlock result => new BetaToolResultBlockParam
        {
            ToolUseID = result.ToolUseId,
            Content = result.Content,
            IsError = result.IsError
        },

        _ => throw new NotSupportedException($"未支援的內容區塊型別：{block.GetType().Name}")
    };

    /// SDK 的工具參數是「屬性字典」而不是單一 JSON 值，回送時要展開
    private static IReadOnlyDictionary<string, JsonElement> ToInputDictionary(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)
            : new Dictionary<string, JsonElement>();

    private static LlmContentBlock? ToNeutralBlock(BetaContentBlock block)
    {
        if (block.TryPickText(out BetaTextBlock? text))
        {
            return text.Text == GatewayEmptyPlaceholder ? null : new LlmTextBlock(text.Text);
        }

        if (block.TryPickToolUse(out BetaToolUseBlock? toolUse))
        {
            return new LlmToolUseBlock(
                toolUse.ID, toolUse.Name, JsonSerializer.SerializeToElement(toolUse.Input));
        }

        // thinking 等其他區塊在本用途下不需要保留
        return null;
    }
}
