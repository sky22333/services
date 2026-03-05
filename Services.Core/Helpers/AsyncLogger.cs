using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Services.Core.Helpers
{
    public class AsyncLogger : IDisposable
    {
        private readonly string _logPath;
        private readonly BlockingCollection<string> _logQueue = new BlockingCollection<string>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _writeTask;

        public AsyncLogger(string logPath)
        {
            _logPath = logPath;
            _writeTask = Task.Run(ProcessQueue);
        }

        public void Log(string message)
        {
            if (!_cts.IsCancellationRequested)
            {
                _logQueue.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
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
            catch (Exception)
            {
                // Fallback or ignore
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _logQueue.CompleteAdding();
            try
            {
                _writeTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
            _cts.Dispose();
            _logQueue.Dispose();
        }
    }
}
