using System.Diagnostics;

namespace Aegis.TortureTests.Harness;

/// <summary>Bir senaryonun bir kütüphane için tüm turlarının özeti.</summary>
public sealed record ScenarioOutcome(string Scenario, Library Library, int Rounds, int Passed, VerdictKind Kind, string? FirstFailure, TimeSpan Elapsed);

/// <summary>
/// Senaryoları her kütüphane için farklı tohumlarla tekrar tekrar çalıştırır. Her tur bir bekçiyle (watchdog) sınırlıdır:
/// süre aşımı kilitlenme sayılır. Turlar arasında GC zorlanır ve gözlenmeyen görev istisnaları sayılır (sızan
/// hata = kaldı).
/// </summary>
public sealed class Runner(int rounds, int baseSeed, TextWriter log)
{
    private int _unobserved;

    public async Task<IReadOnlyList<ScenarioOutcome>> RunAsync(IEnumerable<TortureScenario> scenarios)
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Interlocked.Increment(ref _unobserved);
            e.SetObserved();
        };

        var outcomes = new List<ScenarioOutcome>();
        foreach (var scenario in scenarios)
        {
            foreach (var library in scenario.Libraries)
            {
                var outcome = await RunScenarioAsync(scenario, library).ConfigureAwait(false);
                outcomes.Add(outcome);
                log.WriteLine($"{scenario.Name,-44} {library,-10} {outcome.Passed}/{outcome.Rounds} {outcome.Kind,-12} {outcome.FirstFailure}");
            }
        }

        return outcomes;
    }

    private async Task<ScenarioOutcome> RunScenarioAsync(TortureScenario scenario, Library library)
    {
        var watch = Stopwatch.StartNew();
        var passed = 0;
        string? firstFailure = null;
        for (var round = 0; round < rounds; round++)
        {
            var seed = HashCode.Combine(baseSeed, scenario.Name, round);
            var verdict = await RunRoundAsync(scenario, library, seed).ConfigureAwait(false);
            if (verdict.Kind == VerdictKind.NotSupported)
            {
                return new(scenario.Name, library, rounds, 0, VerdictKind.NotSupported, verdict.Reason, watch.Elapsed);
            }

            if (verdict.Kind == VerdictKind.Pass)
            {
                passed++;
            }
            else
            {
                firstFailure ??= $"tur {round} (tohum {seed}): {verdict.Reason}";
            }
        }

        return new(scenario.Name, library, rounds, passed, passed == rounds ? VerdictKind.Pass : VerdictKind.Fail, firstFailure, watch.Elapsed);
    }

    private async Task<Verdict> RunRoundAsync(TortureScenario scenario, Library library, int seed)
    {
        var unobservedBefore = Volatile.Read(ref _unobserved);
        Task run;
        try
        {
            run = Task.Run(() => scenario.RunAsync(library, new Random(seed)));
        }
        catch (Exception ex)
        {
            return Classify(ex);
        }

        var finished = await Task.WhenAny(run, Task.Delay(scenario.Budget)).ConfigureAwait(false);
        if (finished != run)
        {
            _ = run.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default); // terk edilen turun istisnası sızmasın
            return Verdict.Fail($"kilitlendi: {scenario.Budget.TotalSeconds:F0} sn içinde bitmedi");
        }

        try
        {
            await run.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Classify(ex);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var leaked = Volatile.Read(ref _unobserved) - unobservedBefore;
        return leaked == 0 ? Verdict.Pass : Verdict.Fail($"{leaked} gözlenmeyen görev istisnası sızdı");
    }

    private static Verdict Classify(Exception ex) => ex switch
    {
        NotSupportedByLibraryException notSupported => Verdict.NotSupported(notSupported.Message),
        InvariantViolationException violation => Verdict.Fail(violation.Message),
        _ => Verdict.Fail($"beklenmeyen {ex.GetType().Name}: {ex.Message}")
    };
}

/// <summary>Kütüphane bu yeteneği genel API'sinde sunmuyor.</summary>
public sealed class NotSupportedByLibraryException(string message) : Exception(message);
