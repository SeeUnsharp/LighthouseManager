using System.Runtime.InteropServices;

namespace LighthouseManagerService.Dimming;

/// <summary>
///     Checks before loading a native library found through another process (Linux). The library runs with this
///     user's rights, so neither the process it was found through nor the files may be controlled by another user.
/// </summary>
internal static unsafe class TrustedFiles
{
    private const int AtFdCwd = -100;
    private const uint StatxMode = 0x2;
    private const uint StatxUid = 0x8;

    /// <summary>
    ///     S_IWGRP | S_IWOTH (octal 022).
    /// </summary>
    private const uint GroupOrOtherWritable = 0b000_010_010;

    public static uint CurrentUid => geteuid();

    /// <summary>
    ///     Whether the process runs as this user (real and effective uid). Other users' processes are visible in /proc,
    ///     and their names and command lines are theirs to choose.
    /// </summary>
    public static bool IsOwnProcess(int pid)
    {
        var uid = File.ReadLines($"/proc/{pid}/status").FirstOrDefault(l => l.StartsWith("Uid:"));
        return uid != null && IsOwnUidLine(uid, CurrentUid);
    }

    /// <param name="line">The "Uid:" line of /proc/&lt;pid&gt;/status: real, effective, saved and filesystem uid.</param>
    internal static bool IsOwnUidLine(string line, uint currentUid)
    {
        var ids = line["Uid:".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return ids.Length >= 2 && ids[0] == currentUid.ToString() && ids[1] == currentUid.ToString();
    }

    /// <summary>
    ///     Whether the file (and the target of its symbolic links) and every directory above it belong to this user or
    ///     root and aren't writable by others, so no other user can have placed or replaced it.
    /// </summary>
    public static bool IsTrustedPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var target = File.ResolveLinkTarget(full, true)?.FullName ?? full;
            return IsTrustedChain(full) && IsTrustedChain(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsTrusted(uint owner, uint mode, uint currentUid)
    {
        return (owner == currentUid || owner == 0) && (mode & GroupOrOtherWritable) == 0;
    }

    private static bool IsTrustedChain(string path)
    {
        var uid = CurrentUid;
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (!TryStat(current, out var owner, out var mode) || !IsTrusted(owner, mode, uid)) return false;
        }

        return true;
    }

    private static bool TryStat(string path, out uint owner, out uint mode)
    {
        // struct statx has the same layout on every architecture (unlike struct stat): stx_uid at 20, stx_mode at 28
        var buffer = stackalloc byte[256];
        if (statx(AtFdCwd, path, 0, StatxMode | StatxUid, buffer) != 0)
        {
            owner = mode = 0;
            return false;
        }

        owner = *(uint*)(buffer + 20);
        mode = *(ushort*)(buffer + 28);
        return true;
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc")]
    private static extern int statx(int dirfd, string path, int flags, uint mask, byte* buffer);
}
