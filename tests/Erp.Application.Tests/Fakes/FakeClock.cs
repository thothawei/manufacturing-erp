using Erp.Application.Common;

namespace Erp.Application.Tests.Fakes;

public sealed class FakeClock(DateOnly today) : IClock
{
    public DateOnly Today { get; } = today;
}
