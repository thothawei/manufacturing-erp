using System.Reflection;
using NetArchTest.Rules;

namespace Erp.ArchitectureTests;

/// 架構邊界的測試。
///
/// 「Domain 不知道 AI 的存在」如果只寫在文件裡，遲早會有人加一個 using 就破功。
/// 這組測試讓邊界被 CI 保護，而不是靠自律。
public class LayerDependencyTests
{
    private static readonly Assembly Domain = typeof(Erp.Domain.Items.Item).Assembly;
    private static readonly Assembly Application = typeof(Erp.Application.Bom.BomExplosionService).Assembly;
    private static readonly Assembly Infrastructure = typeof(Erp.Infrastructure.AI.ToolCatalog).Assembly;

    [Fact]
    public void Domain不得相依於任何其他層()
    {
        var result = Types.InAssembly(Domain)
            .Should()
            .NotHaveDependencyOnAny("Erp.Application", "Erp.Infrastructure", "Erp.Api")
            .GetResult();

        AssertSuccess(result);
    }

    [Fact]
    public void Application不得相依於Infrastructure()
    {
        // 依賴反轉的核心：Application 定義介面，Infrastructure 實作，方向不能反過來
        var result = Types.InAssembly(Application)
            .Should()
            .NotHaveDependencyOnAny("Erp.Infrastructure", "Erp.Api")
            .GetResult();

        AssertSuccess(result);
    }

    [Fact]
    public void Domain與Application都不得相依於AI子系統()
    {
        foreach (var assembly in new[] { Domain, Application })
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn("Erp.Infrastructure.AI")
                .GetResult();

            AssertSuccess(result, assembly.GetName().Name);
        }
    }

    [Fact]
    public void Domain與Application都不得參考任何LLM廠商的套件()
    {
        // 用組件參考檢查，比命名空間檢查更難繞過：
        // 只要 csproj 加了 PackageReference 並實際用到，這裡就會抓到
        string[] llmVendors = ["Anthropic", "OpenAI", "Azure.AI", "Google.Cloud.AIPlatform"];

        foreach (var assembly in new[] { Domain, Application })
        {
            var referenced = assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? "")
                .Where(name => llmVendors.Any(v => name.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Assert.True(referenced.Count == 0,
                $"{assembly.GetName().Name} 參考了 LLM 廠商套件：{string.Join("、", referenced)}");
        }
    }

    [Fact]
    public void Domain不得參考資料存取套件()
    {
        // EF Core 屬於 Infrastructure 的細節，Domain 實體必須是純 POCO
        var referenced = Domain.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(referenced.Count == 0,
            $"Erp.Domain 參考了 EF Core：{string.Join("、", referenced)}");
    }

    [Fact]
    public void AI子系統不得被Persistence相依()
    {
        // 兩個子系統是平行的，Persistence 不該知道 AI 存在
        var result = Types.InAssembly(Infrastructure)
            .That().ResideInNamespace("Erp.Infrastructure.Persistence")
            .Should()
            .NotHaveDependencyOn("Erp.Infrastructure.AI")
            .GetResult();

        AssertSuccess(result);
    }

    [Fact]
    public void AI助理的實作必須透過Application定義的介面()
    {
        var implementations = Types.InAssembly(Infrastructure)
            .That().ImplementInterface(typeof(Erp.Application.Abstractions.IAiAssistantService))
            .GetTypes()
            .ToList();

        Assert.NotEmpty(implementations);
        Assert.All(implementations, type =>
            Assert.StartsWith("Erp.Infrastructure.AI", type.Namespace));
    }

    private static void AssertSuccess(TestResult result, string? context = null)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        var offenders = string.Join("、", result.FailingTypeNames ?? []);
        Assert.Fail($"{context ?? "架構規則"}違規的型別：{offenders}");
    }
}
