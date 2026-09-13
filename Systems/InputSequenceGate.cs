namespace AutoExile.Systems
{
    // A time estimate cannot own asynchronous input. Keep ownership until cleanup completes,
    // including after cancellation, so a previous sequence cannot release a new click's button.
    public sealed class InputSequenceGate
    {
        private readonly object _sync = new();
        private CancellationTokenSource? _cancellation;
        public bool IsRunning { get { lock (_sync) return _cancellation != null; } }

        public void Cancel()
        {
            lock (_sync) _cancellation?.Cancel();
        }

        public async Task RunAsync(Func<CancellationToken, Task> action, TimeSpan? timeout = null)
        {
            CancellationTokenSource cancellation;
            lock (_sync)
            {
                if (_cancellation != null) throw new InvalidOperationException("Input sequence already running");
                _cancellation = cancellation = new CancellationTokenSource();
                if (timeout.HasValue) cancellation.CancelAfter(timeout.Value);
            }
            try
            {
                // ConfigureAwait(false) alone can run the entire synchronous prefix on
                // ExileAPI's Tick thread. Yield even when the first input delay is zero.
                // Ownership is acquired BEFORE dispatch and retained through cleanup.
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                await action(cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    _cancellation = null;
                    cancellation.Dispose();
                }
            }
        }
    }
}
