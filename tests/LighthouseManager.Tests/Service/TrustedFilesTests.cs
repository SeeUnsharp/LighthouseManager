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
        Assert.True(TrustedFiles.IsTrusted(1000, 0b111_101_101, 1000)); // 0755
        Assert.True(TrustedFiles.IsTrusted(0, 0b110_100_100, 1000)); // 0644, root
        Assert.False(TrustedFiles.IsTrusted(1001, 0b111_101_101, 1000)); // another user
        Assert.False(TrustedFiles.IsTrusted(1000, 0b111_111_101, 1000)); // 0775
        Assert.False(TrustedFiles.IsTrusted(0, 0b1_111_111_111, 1000)); // 1777 like /tmp
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
}
