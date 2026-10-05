using System.Text;

namespace Aegis.TortureTests.Harness;

/// <summary>Sonuçları Markdown geçti/kaldı matrisine ve ayrıntılı hata listesine çevirir.</summary>
public static class Report
{
    private static readonly Library[] Columns = [Library.Aegis, Library.Polly, Library.Microsoft];

    public static string ToMarkdown(IReadOnlyList<TortureScenario> scenarios, IReadOnlyList<ScenarioOutcome> outcomes, int rounds, int seed)
    {
        var text = new StringBuilder()
            .AppendLine("# Batırma (torture) test sonuçları")
            .AppendLine()
            .AppendLine($"Tarih: {DateTime.Now:yyyy-MM-dd HH:mm} · Tur: {rounds} · Taban tohum: {seed} · .NET {Environment.Version} · {Environment.ProcessorCount} çekirdek")
            .AppendLine()
            .AppendLine("Rakipler: Polly.Core 8.8.0, Polly.RateLimiting 8.8.0, Microsoft.Extensions.Http.Resilience 10.10.0 (NuGet'te en son sürüm).")
            .AppendLine()
            .AppendLine("| Senaryo | Aegis | Polly 8.8.0 | MS Http.Resilience 10.10.0 |")
            .AppendLine("|---|---|---|---|");

        foreach (var scenario in scenarios)
        {
            text.Append($"| {scenario.Name} |");
            foreach (var library in Columns)
            {
                var outcome = outcomes.FirstOrDefault(o => o.Scenario == scenario.Name && o.Library == library);
                text.Append(' ').Append(Cell(outcome)).Append(" |");
            }

            text.AppendLine();
        }

        text.AppendLine().AppendLine("✅ tüm turlar geçti · ❌ en az bir tur kaldı · — kütüphane bu yeteneği sunmuyor / senaryo uygulanmıyor").AppendLine();

        var failures = outcomes.Where(o => o.Kind == VerdictKind.Fail).ToList();
        text.AppendLine("## Kalan turların ilk nedeni").AppendLine();
        if (failures.Count == 0)
        {
            text.AppendLine("Kalan tur yok.");
        }

        foreach (var failure in failures)
        {
            text.AppendLine($"- **{failure.Scenario} / {failure.Library}** ({failure.Passed}/{failure.Rounds}): {failure.FirstFailure}");
        }

        text.AppendLine().AppendLine("## Senaryolar").AppendLine();
        foreach (var scenario in scenarios)
        {
            text.AppendLine($"- **{scenario.Name}**: {scenario.Description}");
        }

        return text.ToString();
    }

    private static string Cell(ScenarioOutcome? outcome) => outcome switch
    {
        null => "—",
        { Kind: VerdictKind.NotSupported } => "—",
        { Kind: VerdictKind.Pass } => $"✅ {outcome.Passed}/{outcome.Rounds}",
        _ => $"❌ {outcome.Passed}/{outcome.Rounds}"
    };
}
