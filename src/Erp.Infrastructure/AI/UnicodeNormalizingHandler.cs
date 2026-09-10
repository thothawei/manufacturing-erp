using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Erp.Infrastructure.AI;

/// SDK 內部用預設的 JSON 編碼器序列化請求，會把中文逃逸成 \uXXXX。
/// 系統提示詞、工具說明與使用者問題都是中文，實測請求體積是解碼後的 2.28 倍，
/// 而工具說明每一輪都會重送 —— 這是每次呼叫都在付的 token 成本。
///
/// SDK 沒有提供序列化選項，所以在送出前把 body 重新序列化一次。
/// JSON 語意完全相同，只是不再逃逸非 ASCII 字元。
internal sealed class UnicodeNormalizingHandler : DelegatingHandler
{
    private static readonly JsonSerializerOptions RelaxedOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public UnicodeNormalizingHandler() : base(new HttpClientHandler()) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var original = await request.Content.ReadAsStringAsync(cancellationToken);
            var contentType = request.Content.Headers.ContentType;

            if (TryNormalize(original, out var normalized))
            {
                request.Content = new StringContent(normalized, Encoding.UTF8);
                request.Content.Headers.ContentType =
                    contentType ?? new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    /// 只在確定是合法 JSON 時改寫，其他內容原樣送出
    private static bool TryNormalize(string body, out string normalized)
    {
        normalized = body;

        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(body);
            normalized = JsonSerializer.Serialize(element, RelaxedOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
