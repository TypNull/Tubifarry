using NLog;
using System.Text;

namespace Tubifarry.Core.PythonBridge
{
    /// <summary>
    /// A logger implementation for capturing and managing Python output streams.
    /// </summary>
    public class PythonConsoleLogger : IPythonLogger, IDisposable
    {
        private readonly StringBuilder _buffer;
        private readonly Logger _logger;
        private readonly bool _isErrorStream;
        private bool _disposed;
        private readonly object _lock = new();

        /// <summary>
        /// Event triggered when new output is written to the logger.
        /// </summary>
        public event EventHandler<string>? OnOutputWritten;

        /// <summary>
        /// Gets all content currently in the buffer.
        /// </summary>
        public string Content
        {
            get
            {
                lock (_lock)
                {
                    return _buffer.ToString();
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PythonConsoleLogger"/> class.
        /// </summary>
        /// <param name="isErrorStream">Whether this logger is for an error stream.</param>
        /// <param name="logger">The logger instance.</param>
        public PythonConsoleLogger(bool isErrorStream, Logger? logger = null)
        {
            _buffer = new StringBuilder();
            _isErrorStream = isErrorStream;
            _logger = logger ?? LogManager.GetCurrentClassLogger();
        }

        /// <summary>
        /// Writes a string to the logger.
        /// This method name must be 'write' to match Python's stdout/stderr interface.
        /// </summary>
        /// <param name="text">The text to write.</param>
        public void write(string text)
        {
            if (_disposed || string.IsNullOrEmpty(text))
                return;

            lock (_lock)
            {
                _buffer.Append(text);

                string logPrefix = _isErrorStream ? "[PythonStderr] " : "[PythonStdout] ";
                if (_isErrorStream)
                    _logger.Debug("{0}{1}", logPrefix, text);
                else
                    _logger.Trace("{0}{1}", logPrefix, text);

                OnOutputWritten?.Invoke(this, text);
            }
        }

        /// <summary>
        /// Writes multiple lines to the logger.
        /// This method name must be 'writelines' to match Python's stdout/stderr interface.
        /// </summary>
        /// <param name="lines">The lines to write.</param>
        public void writelines(string[] lines)
        {
            if (_disposed || lines == null || lines.Length == 0)
                return;

            foreach (string line in lines)
                write(line);
        }

        /// <summary>
        /// Checks if the stream is a terminal/tty.
        /// This method name must be 'isatty' to match Python's stdout/stderr interface.
        /// </summary>
        /// <returns>Always returns true to indicate interactive capability.</returns>
        public bool isatty() => true;

        /// <summary>
        /// Flushes the buffer, clearing its contents.
        /// This method name must be 'flush' to match Python's stdout/stderr interface.
        /// </summary>
        public void flush()
        {
            if (_disposed)
                return;

            lock (_lock)
            {
                _buffer.Clear();
            }
        }

        /// <summary>
        /// Clears the buffer without triggering events.
        /// </summary>
        public void Clear()
        {
            if (_disposed)
                return;

            lock (_lock)
            {
                _buffer.Clear();
            }
        }

        /// <summary>
        /// Closes the logger.
        /// This method name must be 'close' to match Python's stdout/stderr interface.
        /// </summary>
        public void close() => Dispose();

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            lock (_lock)
            {
                _buffer.Clear();
                OnOutputWritten = null;
                _disposed = true;
            }
        }
    }
}