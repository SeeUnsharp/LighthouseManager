#if !WINDOWS
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
    private const uint StatxGid = 0x10;

    // S_IWOTH and S_IWGRP
    private const uint OtherWritable = 0b000_000_010;
    private const uint GroupWritable = 0b000_010_000;

    private static readonly Lazy<uint?> PrivateGroup = new(() => FindPrivateGroup(geteuid(), getegid(),
        ReadLines("/etc/passwd"), ReadLines("/etc/group")));

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
    ///     Whether the file and every directory above it belong to this user or root and can't be changed by other
    ///     users. Checked along the path as given and along the fully resolved one (symbolic links anywhere in it),
    ///     so neither the links nor their targets can be swapped by someone else.
    /// </summary>
    public static bool IsTrustedPath(string path)
    {
        var full = Path.GetFullPath(path);
        return RealPath(full) is { } real && IsTrustedChain(full) && IsTrustedChain(real);
    }

    /// <param name="privateGroup">This user's private group (see <see cref="FindPrivateGroup" />), if any.</param>
    internal static bool IsTrusted(uint owner, uint group, uint mode, uint currentUid, uint? privateGroup)
    {
        return (owner == currentUid || owner == 0) && (mode & OtherWritable) == 0 &&
               ((mode & GroupWritable) == 0 || group == privateGroup);
    }

    /// <summary>
    ///     The user's own group on distributions with user private groups (Debian, Ubuntu, Fedora, ...: umask 002, so
    ///     the user's files are group-writable): a group nobody else is in, neither as primary group nor as member.
    /// </summary>
    /// <returns>Its id, or null if the primary group is shared or unknown.</returns>
    internal static uint? FindPrivateGroup(uint uid, uint gid, IEnumerable<string> passwd, IEnumerable<string> groups)
    {
        if (gid == 0) return null;
        string? user = null;
        foreach (var fields in passwd.Select(l => l.Split(':')).Where(f => f.Length >= 4))
        {
            if (fields[2] == uid.ToString()) user = fields[0];
            else if (fields[3] == gid.ToString()) return null;
        }

        var group = groups.Select(l => l.Split(':')).FirstOrDefault(f => f.Length >= 4 && f[2] == gid.ToString());
        if (user == null || group == null) return null;
        return group[3].Split(',', StringSplitOptions.RemoveEmptyEntries).All(m => m == user) ? gid : null;
    }

    private static bool IsTrustedChain(string path)
    {
        var uid = CurrentUid;
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (!TryStat(current, out var owner, out var group, out var mode) ||
                !IsTrusted(owner, group, mode, uid, PrivateGroup.Value))
                return false;
        }

        return true;
    }

    private static bool TryStat(string path, out uint owner, out uint group, out uint mode)
    {
        // struct statx has the same layout on every architecture (unlike struct stat): stx_uid at 20, stx_gid at 24,
        // stx_mode at 28
        var buffer = stackalloc byte[256];
        if (statx(AtFdCwd, path, 0, StatxMode | StatxUid | StatxGid, buffer) != 0)
        {
            owner = group = mode = 0;
            return false;
        }

        owner = *(uint*)(buffer + 20);
        group = *(uint*)(buffer + 24);
        mode = *(ushort*)(buffer + 28);
        return true;
    }

    /// <returns>The path without symbolic links, "." or "..", or null if it doesn't exist.</returns>
    private static string? RealPath(string path)
    {
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            free(resolved);
        }
    }

    private static IEnumerable<string> ReadLines(string file)
    {
        try
        {
            return File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc")]
    private static extern uint getegid();

    [DllImport("libc")]
    private static extern int statx(int dirfd, string path, int flags, uint mask, byte* buffer);

    [DllImport("libc")]
    private static extern IntPtr realpath(string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);
}
#endif
