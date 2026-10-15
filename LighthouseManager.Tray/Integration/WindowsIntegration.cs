using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace LighthouseManager.Tray.Integration;

/// <summary>
///     Windows: the service runs as a scheduled task at login (under the user, no administrator rights needed), the
///     tray starts through the HKCU Run key. Velopack's Setup.exe creates the Start menu entry.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsIntegration
{
    private const string TaskName = "LighthouseManager";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "LighthouseManager.Tray";

    // Velopack installs to %LOCALAPPDATA%\LighthouseManager\current\, with a stub of the main executable one level up
    public static bool IsInstalled => File.Exists(Path.Combine(AppRoot, "Update.exe"));

    private static string AppRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

    public static void Install()
    {
        RegisterTask();
        StartService();

        // The stub in the app root survives updates, unlike the versioned files in current\
        var stub = Path.Combine(AppRoot, "LighthouseManager.Tray.exe");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(RunValue, $"\"{(File.Exists(stub) ? stub : Environment.ProcessPath)}\"");
    }

    public static void Uninstall()
    {
        StopService();
        SystemIntegration.Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(RunValue, false);
    }

    public static void StopService() => SystemIntegration.Run("schtasks.exe", "/End", "/TN", TaskName);

    public static void StartService() => SystemIntegration.Run("schtasks.exe", "/Run", "/TN", TaskName);

    private static void RegisterTask()
    {
        // current\ keeps its path across updates
        var service = Path.Combine(AppContext.BaseDirectory, SystemIntegration.ServiceExecutable + ".exe");
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";

        // ExecutionTimeLimit PT0S: without it Windows stops the task after 3 days
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>LighthouseManager service: wakes and sleeps Lighthouse base stations.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(service)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(AppContext.BaseDirectory)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), $"lighthousemanager-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            SystemIntegration.Run("schtasks.exe", "/Create", "/TN", TaskName, "/XML", file, "/F");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
