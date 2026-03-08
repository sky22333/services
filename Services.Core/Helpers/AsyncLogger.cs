using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Services.Core.Helpers
{
    public class AsyncLogger : IAsyncDisposable, IDisposable
    {
        private readonly string _logPath;
        private readonly BlockingCollection<string> _logQueue = new BlockingCollection<string>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _writeTask;
        private bool _disposed;

        public AsyncLogger(string logPath)
        {
            _logPath = logPath;
            _writeTask = Task.Run(ProcessQueue);
        }

        public void Log(string message)
        {
            if (!_cts.IsCancellationRequested && !_disposed)
            {
                try
                {
                    _logQueue.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
                }
                catch (InvalidOperationException)
                {
                    // Queue已关闭，忽略
                }
            }
        }

        private void ProcessQueue()
        {
            try
            {
                using (var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(fs) { AutoFlush = true })
                {
                    foreach (var line in _logQueue.GetConsumingEnumerable(_cts.Token))
                    {
                        writer.WriteLine(line);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AsyncLogger ProcessQueue error: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            
            _disposed = true;
            _cts.Cancel();
            _logQueue.CompleteAdding();
            
            try
            {
                await _writeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AsyncLogger DisposeAsync error: {ex.Message}");
            }
            
            _cts.Dispose();
            _logQueue.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            _cts.Cancel();
            _logQueue.CompleteAdding();
            
            try
            {
                _writeTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AsyncLogger Dispose error: {ex.Message}");
            }
            
            _cts.Dispose();
            _logQueue.Dispose();
            GC.SuppressFinalize(this);
        }

        ~AsyncLogger()
        {
            Dispose();
        }
    }
}
