using LighthouseManager.Core;

namespace LighthouseManager.Tests.Core;

public class AtomicFileTests
{
    [Fact]
    public void Writes_and_replaces_the_file_and_leaves_no_temporary_file()
    {
        var directory = Directory.CreateTempSubdirectory("atomic-");
        var file = Path.Combine(directory.FullName, "sub", "settings.json");
        try
        {
            AtomicFile.WriteAllText(file, "one");
            AtomicFile.WriteAllText(file, "two");

            Assert.Equal("two", File.ReadAllText(file));
            Assert.False(File.Exists(file + ".tmp"));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void Owner_only_also_when_a_temporary_file_was_left_behind()
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = Directory.CreateTempSubdirectory("atomic-");
        var file = Path.Combine(directory.FullName, "mqtt.json");
        try
        {
            // Left by a crash, readable by everyone
            File.WriteAllText(file + ".tmp", "old");
            File.SetUnixFileMode(file + ".tmp", (UnixFileMode)0b110_110_110);

            AtomicFile.WriteAllText(file, "secret", ownerOnly: true);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
