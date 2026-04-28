namespace HomeYarp.Tests.TestHelpers;

/// <summary>
/// IProgress&lt;T&gt; that invokes the callback synchronously on the calling thread.
/// The framework's <see cref="System.Progress{T}"/> dispatches via the captured
/// SynchronizationContext (or thread pool), which makes deterministic assertions
/// flaky in tests. Use this stub instead.
/// </summary>
internal sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _onReport;

    public SynchronousProgress(Action<T> onReport) => _onReport = onReport;

    public void Report(T value) => _onReport(value);
}
