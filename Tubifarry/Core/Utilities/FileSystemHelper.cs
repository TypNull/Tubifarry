namespace Tubifarry.Core.Utilities
{
    public static class FileSystemHelper
    {
        private static readonly char[] InvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*', .. Path.GetInvalidFileNameChars()];

        public static string SanitizeFileName(string name)
        {
            string sanitized = string.Concat(name.Select(c => char.IsControl(c) || InvalidFileNameChars.Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            return sanitized.Length == 0 ? "_" : sanitized[..Math.Min(sanitized.Length, 150)];
        }

        public static void ReplaceDirectory(string source, string target)
        {
            string? parent = Path.GetDirectoryName(Path.GetFullPath(target));
            if (parent != null)
                Directory.CreateDirectory(parent);

            string? backup = null;
            if (Directory.Exists(target))
            {
                backup = $"{target}.old-{Guid.NewGuid():N}";
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(source, target);
            }
            catch
            {
                if (backup != null && !Directory.Exists(target))
                    Directory.Move(backup, target);
                throw;
            }

            if (backup != null)
                TryDeleteDirectory(backup);
        }

        public static bool TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static bool TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
