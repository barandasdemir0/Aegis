namespace Aegis.Resilience.Extensions.DependencyInjection.Aop;

/// <summary>AOT/trim uyarı metinleri (tek yerde).</summary>
internal static class AegisAotMessages
{
    public const string DispatchProxy =
        "[AegisPolicy] AOP'si System.Reflection.DispatchProxy kullanır ve çalışma anında kod üretir; Native AOT ile uyumlu değildir. " +
        "AOT uygulamalarında boru hattını doğrudan kullanın (IAegisPipelineRegistry.GetPipeline(...).ExecuteAsync).";
}
