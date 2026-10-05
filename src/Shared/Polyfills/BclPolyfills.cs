// Yalnızca netstandard2.0 / net462 derlemelerine eklenir (Directory.Build.targets).
// .NET 6-8 BCL API'lerinin eski hedeflerdeki karşılıkları. C# 14 extension üyeleri sayesinde çağıran kod değişmez:
// ArgumentNullException.ThrowIfNull(x), ValueTask.FromResult(x), Random.Shared, task.IsCompletedSuccessfully ... iki dünyada
// da aynen derlenir. Davranış .NET 8'dekiyle aynıdır; farklı olan tek yer TryReset (aşağıda açıklandı).
#pragma warning disable CA1822

global using Aegis.Polyfills;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Aegis.Polyfills;

internal static class BclPolyfills
{
    extension(ArgumentNullException)
    {
        public static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }
        }
    }

    extension(ArgumentException)
    {
        public static void ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            ArgumentNullException.ThrowIfNull(argument, paramName);
            if (argument.Length == 0)
            {
                throw new ArgumentException("The value cannot be an empty string.", paramName);
            }
        }

        public static void ThrowIfNullOrWhiteSpace([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            ArgumentNullException.ThrowIfNull(argument, paramName);
            if (string.IsNullOrWhiteSpace(argument))
            {
                throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", paramName);
            }
        }
    }

    extension(ArgumentOutOfRangeException)
    {
        public static void ThrowIfNegative(int value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(paramName, value, "The value must be non-negative.");
            }
        }

        public static void ThrowIfNegativeOrZero(int value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(paramName, value, "The value must be positive.");
            }
        }
    }

    extension(ObjectDisposedException)
    {
        public static void ThrowIf([DoesNotReturnIf(true)] bool condition, object instance)
        {
            if (condition)
            {
                throw new ObjectDisposedException(instance.GetType().FullName);
            }
        }
    }

    extension(System.Runtime.ExceptionServices.ExceptionDispatchInfo)
    {
        /// <summary>.NET 5 <c>ExceptionDispatchInfo.Throw(Exception)</c>: özgün yığın izini koruyarak yeniden fırlatır.</summary>
        [DoesNotReturn]
        public static void Throw(Exception source)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(source).Throw();
            throw source; // ulaşılmaz; derleyicinin akış analizi için
        }
    }

    extension(Random)
    {
        /// <summary>İş parçacığı güvenli paylaşılan örnek (.NET 6 <c>Random.Shared</c>): iş parçacığı başına ayrı örnek.</summary>
        public static Random Shared => ThreadLocalRandom.Instance;
    }

    extension(Stopwatch)
    {
        public static TimeSpan GetElapsedTime(long startingTimestamp) =>
            GetElapsedTimeCore(startingTimestamp, Stopwatch.GetTimestamp());

        public static TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            GetElapsedTimeCore(startingTimestamp, endingTimestamp);
    }

    private static TimeSpan GetElapsedTimeCore(long start, long end) =>
        new((long)((end - start) * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));

    extension(ValueTask)
    {
        public static ValueTask CompletedTask => default;

        public static ValueTask<TResult> FromResult<TResult>(TResult result) => new(result);

        public static ValueTask<TResult> FromException<TResult>(Exception exception) =>
            new(Task.FromException<TResult>(exception));
    }

    extension(Task)
    {
        /// <summary>.NET 8 <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c> (Microsoft.Bcl.TimeProvider ile).</summary>
        public static Task Delay(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            timeProvider.Delay(delay, cancellationToken);
    }

    extension(Task task)
    {
        public bool IsCompletedSuccessfully => task.Status == TaskStatus.RanToCompletion;

        /// <summary>.NET 6 <c>Task.WaitAsync(CancellationToken)</c>.</summary>
        public Task WaitAsync(CancellationToken cancellationToken) =>
            task.IsCompleted || !cancellationToken.CanBeCanceled ? task : WaitCoreAsync(task, cancellationToken);
    }

    extension(Task task)
    {
        /// <summary>.NET 6 <c>Task.WaitAsync(TimeSpan, CancellationToken)</c>: süre dolarsa <see cref="TimeoutException"/>.</summary>
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            task.IsCompleted ? task : WaitCoreAsync(task, timeout, cancellationToken);
    }

    extension<TResult>(Task<TResult> task)
    {
        /// <summary>.NET 6 <c>Task&lt;T&gt;.WaitAsync(CancellationToken)</c>.</summary>
        public async Task<TResult> WaitAsync(CancellationToken cancellationToken)
        {
            await ((Task)task).WaitAsync(cancellationToken).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        /// <summary>.NET 6 <c>Task&lt;T&gt;.WaitAsync(TimeSpan, CancellationToken)</c>.</summary>
        public async Task<TResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await ((Task)task).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
    }

    private static Task WaitCoreAsync(Task task, CancellationToken cancellationToken) =>
        WaitCoreAsync(task, Timeout.InfiniteTimeSpan, cancellationToken);

    private static async Task WaitCoreAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutSource.CancelAfter(timeout);
        }

        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (timeoutSource.Token.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), stopped))
        {
            if (await Task.WhenAny(task, stopped.Task).ConfigureAwait(false) != task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
        }

        await task.ConfigureAwait(false);
    }

    extension(string text)
    {
        /// <summary>.NET Core 2.1 <c>IndexOf(char, StringComparison)</c> (karakter aramasında yalnızca Ordinal anlamlıdır).</summary>
        public int IndexOf(char value, StringComparison comparisonType) =>
            comparisonType == StringComparison.Ordinal ? text.IndexOf(value) : text.IndexOf(value.ToString(), comparisonType);
    }

    extension(CancellationToken token)
    {
        /// <summary>.NET Core 3.0 <c>UnsafeRegister</c>: eski hedeflerde ExecutionContext akışı olan Register'a düşer (davranış aynı).</summary>
        public CancellationTokenRegistration UnsafeRegister(Action<object?> callback, object? state) =>
            token.Register(callback, state);
    }

    extension(CancellationTokenSource source)
    {
        /// <summary>
        /// .NET 6 <c>TryReset</c>. Eski hedeflerde sıfırlanamaz: false döner, böylece havuz kaynağı dispose eder ve yenisini
        /// oluşturur (doğru, yalnızca havuzlamanın tahsis kazancı yok).
        /// </summary>
        public bool TryReset() => false;
    }

    extension<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
    {
        public void Deconstruct(out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }

    private static class ThreadLocalRandom
    {
        [ThreadStatic]
        private static Random? t_instance;

        public static Random Instance => t_instance ??= new Random(Guid.NewGuid().GetHashCode());
    }
}
