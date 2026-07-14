using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace RevitPointCloudMCP
{
    /// <summary>
    /// The Revit API can only be called from Revit's main thread. Our MCP HTTP
    /// server runs on background threads (one per request). This class is the
    /// bridge between the two: background threads call RunAsync(...), which
    /// queues the work and raises an ExternalEvent; Revit calls back into
    /// Execute(...) on its own thread when it's ready, and the original caller's
    /// Task completes with the result.
    /// </summary>
    public sealed class RevitCommandDispatcher : IExternalEventHandler
    {
        private sealed class Job
        {
            public required Func<UIApplication, object?> Work { get; init; }
            public TaskCompletionSource<object?> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly ConcurrentQueue<Job> _queue = new();
        private ExternalEvent? _externalEvent;

        /// <summary>Must be called from Revit's main thread (e.g. from OnStartup).</summary>
        public void Register()
        {
            _externalEvent = ExternalEvent.Create(this);
        }

        public void Unregister()
        {
            _externalEvent?.Dispose();
            _externalEvent = null;
        }

        /// <summary>
        /// Queues <paramref name="work"/> to run on Revit's main thread and
        /// asynchronously waits for it to finish. Safe to call from any thread.
        /// </summary>
        public async Task<object?> RunAsync(Func<UIApplication, object?> work, TimeSpan timeout)
        {
            if (_externalEvent is null)
                throw new InvalidOperationException("Dispatcher not registered.");

            var job = new Job { Work = work };
            _queue.Enqueue(job);
            _externalEvent.Raise();

            var delay = Task.Delay(timeout);
            var winner = await Task.WhenAny(job.Completion.Task, delay);
            if (winner == delay)
            {
                throw new TimeoutException(
                    "Revit did not respond in time. Check whether a modal dialog " +
                    "(e.g. a warning, or the Family Editor) is open and blocking the UI thread.");
            }

            return await job.Completion.Task; // rethrows the original exception on failure
        }

        /// <summary>Called by Revit on its main thread after Raise().</summary>
        public void Execute(UIApplication app)
        {
            // Drain everything that's queued up right now - multiple requests may
            // have piled up between idle ticks.
            while (_queue.TryDequeue(out var job))
            {
                try
                {
                    var result = job.Work(app);
                    job.Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    job.Completion.TrySetException(ex);
                }
            }
        }

        public string GetName() => "Point Cloud MCP Bridge";
    }
}
