namespace LighthouseManager.Core;

/// <summary>
///     Writes a temporary file next to the target first and then replaces the target, so a crash can't leave a half
///     written file.
/// </summary>
public static class AtomicFile
{
    /// <param name="ownerOnly">Readable by the owner only (Linux, mode 0600), e.g. for passwords.</param>
    public static void WriteAllText(string path, string contents, bool ownerOnly = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        // A leftover from a crash would keep its permissions, the create mode only applies to new files
        File.Delete(temp);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (ownerOnly && !OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(contents);

        File.Move(temp, path, true);
    }
}
