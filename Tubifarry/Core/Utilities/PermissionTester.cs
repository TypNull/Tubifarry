using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;

namespace Tubifarry.Core.Utilities
{
    public static class PermissionTester
    {
        public static ValidationFailure? TestReadWritePermissions(string directoryPath, ILogger logger)
        {
            string testFilePath = Path.Combine(directoryPath, "test_permissions.tmp");
            try
            {
                File.WriteAllText(testFilePath, "This is a test file to check write permissions.");
                logger.Trace("Write permission test succeeded.");
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.Warn(ex, "Write permission denied for directory");
                return new ValidationFailure("DirectoryPath", $"Write permission denied for directory: {ex.Message}");
            }

            try
            {
                string content = File.ReadAllText(testFilePath);
                logger.Trace("Read permission test succeeded.");
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.Warn(ex, "Read permission denied for directory");
                return new ValidationFailure("DirectoryPath", $"Read permission denied for directory: {ex.Message}");
            }

            try
            {
                File.Delete(testFilePath);
                logger.Trace("Delete permission test succeeded.");
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.Warn(ex, "Delete permission denied for directory");
                return new ValidationFailure("DirectoryPath", $"Delete permission denied for directory: {ex.Message}");
            }
            return null;
        }

        public static ValidationFailure? TestExistance(string directoryPath, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                logger.Warn("Directory path is null or empty.");
                return new ValidationFailure("DirectoryPath", "Directory path cannot be null or empty.");
            }

            if (!Directory.Exists(directoryPath))
            {
                logger.Info("Directory does not exist. Attempting to create it.");
                Directory.CreateDirectory(directoryPath);
            }
            return null;
        }

        public static ValidationFailure? TestExecutePermissions(string directoryPath, ILogger logger)
        {
            try
            {
                string[] files = Directory.GetFiles(directoryPath);
                logger.Info("Execute permission test succeeded.");
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.Warn(ex, "Execute permission denied for directory");
                return new ValidationFailure("DirectoryPath", $"Execute permission denied for directory: {ex.Message}");
            }
            return null;

        }

        public static List<ValidationFailure> TestAllPermissions(string directoryPath, ILogger logger)
        {
            List<ValidationFailure> tests = [];
            try
            {
                tests!.AddIfNotNull(TestExistance(directoryPath, logger));
                tests!.AddIfNotNull(TestReadWritePermissions(directoryPath, logger));
                tests!.AddIfNotNull(TestExecutePermissions(directoryPath, logger));
                logger.Trace("All directory permissions tests succeeded.");
            }
            catch (IOException ex)
            {
                logger.Warn(ex, "IO error while testing directory permissions");
                tests.Add(new ValidationFailure("DirectoryPath", $"IO error while testing directory: {ex.Message}"));
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Unexpected error while testing directory permissions");
                tests.Add(new ValidationFailure("DirectoryPath", $"Unexpected error: {ex.Message}"));
            }
            return tests;
        }
    }
}
