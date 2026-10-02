using System.Diagnostics;

namespace Tubifarry.Core.Utilities
{
    public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool Succeeded => ExitCode == 0;
    }

    public static class ProcessRunner
    {
        public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
        {
            ProcessStartInfo startInfo = new(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            if (environment != null)
            {
                foreach (KeyValuePair<string, string> variable in environment)
                    startInfo.Environment[variable.Key] = variable.Value;
            }

            return startInfo;
        }

        public static Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token = default, IReadOnlyDictionary<string, string>? environment = null) =>
            RunAsync(CreateStartInfo(fileName, arguments, environment), timeout, token);

        public static async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken token = default)
        {
            using Process process = new() { StartInfo = startInfo };
            process.Start();

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(token);

            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutSource.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }

                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"'{Path.GetFileName(startInfo.FileName)}' did not finish within {timeout.TotalMinutes:0.#} minutes.");
            }

            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }
    }
}
