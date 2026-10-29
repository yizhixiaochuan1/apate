namespace apate;

internal enum FileOperation { Disguise, Reveal, RevealLegacy }
internal sealed record BatchProgress(int Completed, int Total, string Name);

internal sealed class BatchResult
{
    internal int Succeeded { get; set; }
    internal bool Cancelled { get; set; }
    internal List<string> Errors { get; } = new();
}

internal static class BatchProcessor
{
    internal static BatchResult Run(IEnumerable<string> paths, FileOperation operation,
        byte[] mask, string extension, IProgress<BatchProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new BatchResult();
        try
        {
            var files = CollectFiles(paths, result.Errors, cancellationToken);
            progress?.Report(new BatchProgress(0, files.Count, ""));
            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string file = files[i];
                try
                {
                    switch (operation)
                    {
                        case FileOperation.Disguise:
                            FileTransformer.Disguise(file, mask, extension, cancellationToken);
                            break;
                        case FileOperation.Reveal:
                            FileTransformer.Reveal(file, cancellationToken);
                            break;
                        case FileOperation.RevealLegacy:
                            FileTransformer.RevealLegacy(file, cancellationToken);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(operation));
                    }
                    result.Succeeded++;
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    result.Errors.Add(file + "：" + ex.Message);
                }
                progress?.Report(new BatchProgress(i + 1, files.Count, Path.GetFileName(file)));
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        return result;
    }

    private static List<string> CollectFiles(IEnumerable<string> paths, List<string> errors,
        CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var visited = new HashSet<string>(comparer);
        var files = new List<string>();
        var pending = new Stack<string>(paths.Reverse());
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = pending.Pop();
            try
            {
                path = Path.GetFullPath(path);
                if (!visited.Add(path))
                    continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add(path + "：已跳过链接，避免重复处理或目录循环。");
                    continue;
                }
                if ((attributes & FileAttributes.Directory) == 0)
                    files.Add(path);
                else
                    foreach (string child in Directory.GetFileSystemEntries(path))
                        pending.Push(child);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                errors.Add(path + "：" + ex.Message);
            }
        }
        return files;
    }

    private static bool IsFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
