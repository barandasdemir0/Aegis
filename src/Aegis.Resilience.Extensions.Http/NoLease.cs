namespace Aegis.Resilience.Extensions.Http;

/// <summary>Nesil kiralaması gerektirmeyen işleyiciler için boş kira (<see cref="HttpResilienceExecutor"/>).</summary>
internal readonly struct NoLease : IDisposable
{
    public void Dispose()
    {
    }
}
