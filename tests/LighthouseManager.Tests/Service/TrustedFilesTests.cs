using LighthouseManagerService.Dimming;

namespace LighthouseManager.Tests.Service;

public class TrustedFilesTests
{
    [Fact]
    public void Only_processes_running_as_this_user_count()
    {
        Assert.True(TrustedFiles.IsOwnUidLine("Uid:\t1000\t1000\t1000\t1000", 1000));
        // Another user's process, or one that only switched its effective uid
        Assert.False(TrustedFiles.IsOwnUidLine("Uid:\t1001\t1001\t1001\t1001", 1000));
        Assert.False(TrustedFiles.IsOwnUidLine("Uid:\t1001\t1000\t1000\t1000", 1000));
        Assert.False(TrustedFiles.IsOwnUidLine("Uid:", 1000));
    }

    [Fact]
    public void Files_must_belong_to_this_user_or_root_and_not_be_writable_by_others()
    {
        Assert.True(TrustedFiles.IsTrusted(1000, 1000, 0b111_101_101, 1000, null)); // 0755
        Assert.True(TrustedFiles.IsTrusted(0, 0, 0b110_100_100, 1000, null)); // 0644, root
        Assert.False(TrustedFiles.IsTrusted(1001, 1001, 0b111_101_101, 1000, null)); // another user
        Assert.False(TrustedFiles.IsTrusted(1000, 100, 0b111_111_101, 1000, null)); // 0775, shared group
        Assert.False(TrustedFiles.IsTrusted(0, 0, 0b1_111_111_111, 1000, null)); // 1777 like /tmp
    }

    [Fact]
    public void Group_writable_is_fine_for_the_users_private_group()
    {
        // umask 002 on distributions with user private groups: drwxrwxr-x daniel:daniel
        Assert.True(TrustedFiles.IsTrusted(1000, 1000, 0b111_111_101, 1000, 1000));
        Assert.False(TrustedFiles.IsTrusted(1000, 100, 0b111_111_101, 1000, 1000));
        Assert.False(TrustedFiles.IsTrusted(1000, 1000, 0b111_111_111, 1000, 1000)); // still not for others
    }

    [Fact]
    public void Private_group_is_one_nobody_else_is_in()
    {
        string[] passwd = ["root:x:0:0::/root:/bin/bash", "daniel:x:1000:1000::/home/daniel:/bin/bash"];

        Assert.Equal(1000u, TrustedFiles.FindPrivateGroup(1000, 1000, passwd, ["daniel:x:1000:"]));
        Assert.Equal(1000u, TrustedFiles.FindPrivateGroup(1000, 1000, passwd, ["daniel:x:1000:daniel"]));
        // Another member, another user with it as primary group, or the group unknown
        Assert.Null(TrustedFiles.FindPrivateGroup(1000, 1000, passwd, ["daniel:x:1000:daniel,mallory"]));
        Assert.Null(TrustedFiles.FindPrivateGroup(1000, 1000, [..passwd, "mallory:x:1001:1000::/home/m:/bin/sh"],
            ["daniel:x:1000:"]));
        Assert.Null(TrustedFiles.FindPrivateGroup(1000, 1000, passwd, []));
        // Shared primary group like "users"
        Assert.Null(TrustedFiles.FindPrivateGroup(1000, 100, ["daniel:x:1000:100::/home/daniel:/bin/bash",
            "mallory:x:1001:100::/home/m:/bin/sh"], ["users:x:100:"]));
    }

    [Fact]
    public void Real_processes_and_paths()
    {
        if (!OperatingSystem.IsLinux()) return;

        Assert.True(TrustedFiles.IsOwnProcess(Environment.ProcessId));
        // init runs as root
        Assert.False(TrustedFiles.IsOwnProcess(1));

        // System folders belong to root
        Assert.True(TrustedFiles.IsTrustedPath("/usr"));

        // Anyone can place files in /tmp
        var file = Path.Combine("/tmp", $"libmonado-{Guid.NewGuid():N}.so");
        File.WriteAllText(file, "");
        try
        {
            Assert.False(TrustedFiles.IsTrustedPath(file));
        }
        finally
        {
            File.Delete(file);
        }

        Assert.False(TrustedFiles.IsTrustedPath("/does/not/exist"));
    }

    [Fact]
    public void Symbolic_links_are_checked_up_to_their_targets()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Next to the test binaries: a folder of this user, unless the checkout itself isn't trusted
        var own = typeof(TrustedFilesTests).Assembly.Location;
        if (!TrustedFiles.IsTrustedPath(own)) return;

        // A trusted path whose folder is a link into /tmp, where anyone can swap the files
        var target = Directory.CreateTempSubdirectory("monado-lib-");
        var link = Path.Combine(Path.GetDirectoryName(own)!, $"monado-link-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(Path.Combine(target.FullName, "libmonado.so"), "");
            Directory.CreateSymbolicLink(link, target.FullName);

            Assert.False(TrustedFiles.IsTrustedPath(Path.Combine(link, "libmonado.so")));
        }
        finally
        {
            // Removes only the link
            if (Directory.Exists(link)) Directory.Delete(link);
            target.Delete(true);
        }
    }
}
