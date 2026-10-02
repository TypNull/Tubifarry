using NLog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Core.Python
{
    public sealed record PythonWorkerProgress(double Value, string? Message);

    public sealed class PythonWorkerException(string type, string message, string traceback) : Exception(message)
    {
        public string PythonType { get; } = type;
        public string PythonTraceback { get; } = traceback;
    }

    public sealed class PythonWorkerUnavailableException(string message, bool requestSent = false) : Exception(message)
    {
        public bool RequestSent { get; } = requestSent;
    }

    public sealed class PythonWorker : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(false);

        private readonly string _name;
        private readonly Logger _logger;
        private readonly Process _process;
        private readonly SemaphoreSlim _callGate = new(1, 1);
        private readonly ConcurrentDictionary<long, PendingCall> _pending = new();
        private readonly TaskCompletionSource<JsonObject> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private long _nextId;
        private int _disposed;
        private volatile bool _outputClosed;

        private sealed record PendingCall(TaskCompletionSource<JsonNode?> Completion, IProgress<PythonWorkerProgress>? Progress);

        public string Key { get; }
        public string EnvironmentDirectory { get; }
        public DateTime LastUsed { get; private set; } = DateTime.UtcNow;
        public bool IsBusy => _callGate.CurrentCount == 0;

        public bool IsAlive
        {
            get
            {
                if (_disposed == 1 || _outputClosed)
                    return false;

                try
                {
                    return !_process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        private PythonWorker(string key, string name, string environmentDirectory, Process process, Logger logger)
        {
            Key = key;
            _name = name;
            EnvironmentDirectory = environmentDirectory;
            _process = process;
            _logger = logger;
            _ready.Task.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        public static async Task<PythonWorker> StartAsync(string key, string name, PythonEnvironment environment, string workerScript, string handlerScript, IReadOnlyDictionary<string, string> variables, TimeSpan startTimeout, Logger logger, CancellationToken token)
        {
            ProcessStartInfo startInfo = ProcessRunner.CreateStartInfo(environment.Python, ["-I", "-X", "utf8", "-u", workerScript, handlerScript], variables);
            startInfo.RedirectStandardInput = true;
            startInfo.StandardInputEncoding = Utf8;
            startInfo.StandardOutputEncoding = Utf8;
            startInfo.StandardErrorEncoding = Utf8;
            startInfo.WorkingDirectory = Path.GetDirectoryName(workerScript)!;

            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();

            PythonWorker worker = new(key, name, environment.Directory, process, logger);
            worker.StartReaders();

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(startTimeout);

            try
            {
                JsonObject ready = await worker._ready.Task.WaitAsync(timeout.Token);
                logger.Debug("Python worker '{0}' started (Python {1}, pid {2})", name, ready["python"], ready["pid"]);
                return worker;
            }
            catch
            {
                worker.Dispose();
                throw;
            }
        }

        public async Task<JsonNode?> InvokeAsync(string method, object? parameters, TimeSpan timeout, IProgress<PythonWorkerProgress>? progress = null, CancellationToken token = default)
        {
            await _callGate.WaitAsync(token);
            long id = Interlocked.Increment(ref _nextId);
            try
            {
                if (!IsAlive)
                    throw new PythonWorkerUnavailableException($"Python worker '{_name}' is not running.");

                LastUsed = DateTime.UtcNow;
                PendingCall call = new(new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously), progress);
                _pending[id] = call;

                if (_outputClosed)
                    throw new PythonWorkerUnavailableException($"Python worker '{_name}' stopped.");

                try
                {
                    string request = JsonSerializer.Serialize(new { id, method, @params = parameters });
                    await _process.StandardInput.WriteLineAsync(request.AsMemory(), token);
                    await _process.StandardInput.FlushAsync(token);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    throw new PythonWorkerUnavailableException($"Python worker '{_name}' could not receive '{method}': {ex.Message}");
                }

                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(timeout);

                try
                {
                    return await call.Completion.Task.WaitAsync(limit.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    _logger.Warn("Python worker '{0}' did not answer '{1}' within {2}, stopping it", _name, method, timeout);
                    Dispose();
                    throw new TimeoutException($"Python worker '{_name}' did not finish '{method}' within {timeout.TotalMinutes:0.#} minutes.");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Dispose();
                throw;
            }
            finally
            {
                _pending.TryRemove(id, out _);
                LastUsed = DateTime.UtcNow;
                _callGate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            try
            {
                if (!_process.HasExited)
                    _process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.Debug(ex, "Stopping Python worker '{0}' failed", _name);
            }

            FailPending(new PythonWorkerUnavailableException($"Python worker '{_name}' was stopped.", true));
            _process.Dispose();
        }

        private void StartReaders()
        {
            _ = Task.Run(ReadOutputAsync);
            _ = Task.Run(ReadErrorsAsync);
        }

        private async Task ReadOutputAsync()
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync() is string line)
                    HandleLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.Trace(ex, "Python worker '{0}' output closed", _name);
            }
            finally
            {
                _outputClosed = true;
            }

            string reason = _disposed == 1 ? "was stopped" : $"exited with code {SafeExitCode()}";
            _ready.TrySetException(new PythonWorkerUnavailableException($"Python worker '{_name}' {reason} before it was ready."));
            FailPending(new PythonWorkerUnavailableException($"Python worker '{_name}' {reason}.", true));

            if (_disposed == 0)
                _logger.Warn("Python worker '{0}' {1}", _name, reason);
        }

        private async Task ReadErrorsAsync()
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync() is string line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        _logger.Debug("[{0}] {1}", _name, line);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.Trace(ex, "Python worker '{0}' error stream closed", _name);
            }
        }

        private void HandleLine(string line)
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject message)
                    HandleMessage(message);
                else
                    _logger.Debug("[{0}] {1}", _name, line);
            }
            catch (JsonException)
            {
                _logger.Debug("[{0}] {1}", _name, line);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Python worker '{0}' sent a message that could not be handled", _name);
            }
        }

        private void HandleMessage(JsonObject message)
        {
            if (GetString(message, "event") == "ready")
            {
                _ready.TrySetResult(message);
                return;
            }

            if (message["id"] is not JsonValue idValue || !idValue.TryGetValue(out long id) || !_pending.TryGetValue(id, out PendingCall? call))
                return;

            if (message.ContainsKey("progress"))
            {
                if (message["progress"] is JsonValue value && value.TryGetValue(out double fraction))
                    call.Progress?.Report(new PythonWorkerProgress(fraction, GetString(message, "message")));
                return;
            }

            if (message["error"] is JsonObject error)
            {
                call.Completion.TrySetException(new PythonWorkerException(
                    GetString(error, "type") ?? "Error",
                    GetString(error, "message") ?? "Unknown Python error",
                    GetString(error, "traceback") ?? string.Empty));
                return;
            }

            call.Completion.TrySetResult(message["result"]?.DeepClone());
        }

        private static string? GetString(JsonObject node, string property) =>
            node[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

        private void FailPending(Exception exception)
        {
            foreach (PendingCall call in _pending.Values)
                call.Completion.TrySetException(exception);
        }

        private string SafeExitCode()
        {
            try
            {
                return _process.HasExited ? _process.ExitCode.ToString() : "unknown";
            }
            catch (InvalidOperationException)
            {
                return "unknown";
            }
        }
    }
}
