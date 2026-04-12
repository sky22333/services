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
        private const long MaxLogFileSize = 50 * 1024 * 1024;  // 50 MB

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
                    _logQueue.Add(message);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private void ProcessQueue()
        {
            try
            {
                FileStream? fs = null;
                StreamWriter? writer = null;
                
                try
                {
                    foreach (var line in _logQueue.GetConsumingEnumerable(_cts.Token))
                    {
                        // 检查文件大小，超过限制则轮转
                        if (fs == null || fs.Length > MaxLogFileSize)
                        {
                            writer?.Dispose();
                            fs?.Dispose();
                            
                            // 生成新文件名
                            string newLogPath = _logPath;
                            if (fs != null && fs.Length > MaxLogFileSize)
                            {
                                var dir = Path.GetDirectoryName(_logPath) ?? "";
                                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(_logPath);
                                var ext = Path.GetExtension(_logPath);
                                newLogPath = Path.Combine(dir, $"{fileNameWithoutExt}_{DateTime.Now:HHmmss}{ext}");
                            }
                            
                            fs = new FileStream(newLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                            writer = new StreamWriter(fs) { AutoFlush = true };
                        }
                        
                        writer?.WriteLine(line);
                    }
                }
                finally
                {
                    writer?.Dispose();
                    fs?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
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
