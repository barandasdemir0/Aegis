namespace Aegis.Resilience.Extensions.DependencyInjection.Aop;

/// <summary>
/// Arayüz veya metot seviyesinde otomatik olarak uygulanacak Aegis boru hattını (Pipeline) belirten deklaratif attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AegisPolicyAttribute : Attribute
{
    public string PipelineName { get; }

    public AegisPolicyAttribute(string pipelineName)
    {
        PipelineName = pipelineName ?? throw new ArgumentNullException(nameof(pipelineName));
    }
}
