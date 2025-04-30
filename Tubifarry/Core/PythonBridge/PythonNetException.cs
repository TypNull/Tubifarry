namespace Tubifarry.Core.PythonBridge
{
    /// <summary>
    /// Exception thrown when there is an error configuring the Python.NET environment.
    /// </summary>
    public class PythonNetConfigurationException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the PythonNetConfigurationException class.
        /// </summary>
        public PythonNetConfigurationException() : base() { }

        /// <summary>
        /// Initializes a new instance of the PythonNetConfigurationException class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public PythonNetConfigurationException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the PythonNetConfigurationException class with a specified error message
        /// and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that is the cause of the current exception.</param>
        public PythonNetConfigurationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Exception thrown when there is an error executing Python code within the Python.NET environment.
    /// </summary>
    public class PythonNetExecutionException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the PythonNetExecutionException class.
        /// </summary>
        public PythonNetExecutionException() : base() { }

        /// <summary>
        /// Initializes a new instance of the PythonNetExecutionException class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public PythonNetExecutionException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the PythonNetExecutionException class with a specified error message
        /// and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that is the cause of the current exception.</param>
        public PythonNetExecutionException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}