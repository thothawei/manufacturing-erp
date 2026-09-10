using Erp.Application.Common;
using Erp.Infrastructure.AI;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Erp.Api.ErrorHandling;

/// 把 Application 層的例外對映成語意正確的 HTTP 狀態碼。
///
/// 沒有這一層時，查無料號會回 500 並在回應體裡吐出完整堆疊與本機絕對路徑。
/// AI 助理那條路徑的錯誤處理做在 ToolDispatcher，REST 端點則靠這裡。
public sealed class ErpExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ErpExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = Map(exception);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            // 未預期的錯誤：全文只進伺服器 log，回應體不帶任何內部細節
            logger.LogError(exception, "處理 {Method} {Path} 時發生未預期的例外",
                httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogInformation("{Method} {Path} 回應 {Status}：{Title}",
                httpContext.Request.Method, httpContext.Request.Path, status, title);
        }

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail
            }
        });
    }

    private static (int Status, string Title, string Detail) Map(Exception exception) => exception switch
    {
        EntityNotFoundException ex =>
            (StatusCodes.Status404NotFound, "查無資料", ex.Message),

        // ArgumentOutOfRangeException 也走這條（繼承自 ArgumentException）
        ArgumentException ex =>
            (StatusCodes.Status400BadRequest, "參數錯誤", ex.Message),

        // 參數合法但這個操作對這筆資料不適用，例如對原物料問可製造量
        InvalidOperationException ex =>
            (StatusCodes.Status409Conflict, "無法執行此操作", ex.Message),

        LlmUnavailableException ex =>
            (StatusCodes.Status503ServiceUnavailable, "AI 助理暫時無法使用", ex.Message),

        _ => (StatusCodes.Status500InternalServerError, "伺服器錯誤",
              "處理請求時發生未預期的錯誤，請稍後再試。")
    };
}
