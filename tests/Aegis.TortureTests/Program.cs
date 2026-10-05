using System.Text;
using Aegis.TortureTests.Harness;
using Aegis.TortureTests.Scenarios;

// Kullanım: dotnet run -c Release -- [tur sayısı = 5] [rapor yolu = torture-report.md] [taban tohum = rastgele]
Console.OutputEncoding = Encoding.UTF8;
var rounds = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 5;
var reportPath = args.Length > 1 ? args[1] : "torture-report.md";
var seed = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : Random.Shared.Next();

TortureScenario[] scenarios =
[
    new ThreadStormScenario(),
    new ConcurrencyLimiterLeakScenario(),
    new SyncOverAsyncScenario(),
    new CallerTokenScenario(),
    new TracingStormScenario(),
    new ExtremeOptionsScenario(),
    new MisbehavingCallbacksScenario(),
    new DisposeRaceScenario(),
    new ReloadStormScenario(),
    new TimeJumpScenario(),
    new RateLimiterAdmissionScenario(),
    new MemorySoakScenario(Console.Out),
    new HedgingResponseLeakScenario(),
    new HttpSyncSendScenario(),
    new GarbageRetryAfterScenario(),
    new NonReplayableBodyScenario()
];

Console.WriteLine($"Batırma testleri: {scenarios.Length} senaryo × {rounds} tur, taban tohum {seed}");
var outcomes = await new Runner(rounds, seed, Console.Out).RunAsync(scenarios);
var markdown = Report.ToMarkdown(scenarios, outcomes, rounds, seed);
await File.WriteAllTextAsync(reportPath, markdown);
Console.WriteLine();
Console.WriteLine($"Rapor: {Path.GetFullPath(reportPath)}");
return outcomes.Any(o => o.Library == Library.Aegis && o.Kind == VerdictKind.Fail) ? 1 : 0;
