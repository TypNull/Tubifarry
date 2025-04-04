namespace Tubifarry.Core.PythonBridge
{
    /// <summary>
    /// Interface for Python logger implementations to ensure consistent usage patterns.
    /// </summary>
    public interface IPythonLogger
    {
        /// <summary>
        /// Event triggered when new output is written to the logger.
        /// </summary>
        event EventHandler<string>? OnOutputWritten;

        /// <summary>
        /// Writes a single string to the logger.
        /// </summary>
        /// <param name="str">The string to write.</param>
        void write(string str);

        /// <summary>
        /// Writes multiple lines to the logger.
        /// </summary>
        /// <param name="lines">An array of strings to write.</param>
        void writelines(string[] lines);

        /// <summary>
        /// Indicates whether the logger is connected to a terminal.
        /// </summary>
        /// <returns>True if connected to a terminal, otherwise false.</returns>
        bool isatty();

        /// <summary>
        /// Clears the internal buffer and resets the stream.
        /// </summary>
        void flush();

        /// <summary>
        /// Closes the logger and releases resources.
        /// </summary>
        void close();
    }
}