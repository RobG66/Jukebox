using System.Diagnostics;
using System.Threading.Tasks;

namespace Jukebox.Extensions;

/// <summary>
/// Extension methods for Task to safely observe and log exceptions in fire-and-forget operations.
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Observes a fire-and-forget Task, logging any exception instead of
    /// letting it propagate to TaskScheduler.UnobservedTaskException (which
    /// silently swallows it by default in .NET 10).
    ///
    /// Use this everywhere the codebase previously had:
    ///     _ = SomeAsyncMethod();
    /// Replace with:
    ///     SomeAsyncMethod().SafeFireAndForget(nameof(SomeAsyncMethod));
    /// </summary>
    /// <param name="task">The task to observe.</param>
    /// <param name="operationName">A name for the operation, used in error logs.</param>
    public static void SafeFireAndForget(this Task task, string operationName)
    {
        // ContinueWith captures both faulted and canceled states.
        // TaskContinuationOptions.None runs synchronously on the faulting thread
        // when possible, which is fine here since we only log.
        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                // Log to Debug.WriteLine — invisible in Release builds.
                Debug.WriteLine($"[SafeFireAndForget] {operationName} failed: {t.Exception.Flatten().Message}");
            }
        }, TaskContinuationOptions.None);
    }
}
