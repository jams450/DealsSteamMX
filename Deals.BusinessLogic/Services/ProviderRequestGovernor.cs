namespace Deals.BusinessLogic.Services;

/// <summary>Compatibility governor for direct-store clients. ITAD and GG.deals use provider-specific budgets.</summary>
public sealed class ProviderRequestGovernor : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        return new Release(gate);
    }

    public void Dispose() => gate.Dispose();
    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
