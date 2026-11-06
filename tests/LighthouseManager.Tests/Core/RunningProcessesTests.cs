using System.Diagnostics;
using LighthouseManager.Core;

namespace LighthouseManager.Tests.Core;

public class RunningProcessesTests
{
    [Fact]
    public void Short_names_are_taken_from_comm()
    {
        Assert.Equal("vrserver", RunningProcesses.ResolveName("vrserver", null));
        Assert.Equal("monado-service", RunningProcesses.ResolveName("monado-service", "/opt/x/bin/monado-service\0"));
    }

    [Fact]
    public void Truncated_names_are_completed_from_the_command_line()
    {
        // comm holds at most 15 characters
        Assert.Equal("Envision-x86_64.AppImage", RunningProcesses.ResolveName("Envision-x86_64",
            "/home/user/.local/bin/Envision-x86_64.AppImage\0"));
        // Scripts: argv[0] is the interpreter
        Assert.Equal("long-script-name.py", RunningProcesses.ResolveName("long-script-nam",
            "/usr/bin/python3\0/opt/long-script-name.py\0--flag\0"));
    }

    [Fact]
    public void Truncated_name_stays_when_the_command_line_does_not_match()
    {
        // E.g. a process that changed its command line, or a kernel thread without one
        Assert.Equal("abcdefghijklmno", RunningProcesses.ResolveName("abcdefghijklmno", "other\0"));
        Assert.Equal("abcdefghijklmno", RunningProcesses.ResolveName("abcdefghijklmno", ""));
    }

    [Fact]
    public void Lists_the_current_process_with_the_same_name_as_dotnet()
    {
        using var self = Process.GetCurrentProcess();

        Assert.Contains(new RunningProcesses.Entry(self.Id, self.ProcessName), RunningProcesses.List());
    }
}
