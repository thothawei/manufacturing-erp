using Erp.Application;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
// Phase 2 才會加上 AI 助理端點與 Infrastructure 的 Repository 實作

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
