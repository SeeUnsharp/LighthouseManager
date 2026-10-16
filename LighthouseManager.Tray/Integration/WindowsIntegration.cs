using System.Runtime.Versioning;
using System.Security;
using System.Xml.Linq;
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

    /// <param name="update">After an update: keep the user's autostart choices instead of enabling both.</param>
    public static void Install(bool update)
    {
        var serviceAutostart = !update || IsServiceAutostartEnabled() != false;
        var trayAutostart = !update || IsTrayAutostartEnabled();

        RegisterTask(serviceAutostart);
        StartService();
        SetTrayAutostart(trayAutostart);
    }

    /// <returns>null if the task doesn't exist.</returns>
    public static bool? IsServiceAutostartEnabled()
    {
        var (exitCode, output) = SystemIntegration.RunWithOutput("schtasks.exe", "/Query", "/TN", TaskName, "/XML");
        if (exitCode != 0) return null;
        try
        {
            // schtasks may prefix a byte order mark
            var xml = XDocument.Parse(output.Trim('\uFEFF', ' ', '\r', '\n'));
            var ns = xml.Root!.Name.Namespace;
            var enabled = xml.Descendants(ns + "LogonTrigger").Elements(ns + "Enabled").FirstOrDefault();
            return enabled == null || enabled.Value.Trim() != "false";
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The task stays enabled either way (a disabled task can't be started on demand either); only its logon
    // trigger is switched
    public static void SetServiceAutostart(bool enabled) => RegisterTask(enabled);

    public static bool IsTrayAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    public static void SetTrayAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            key.DeleteValue(RunValue, false);
            return;
        }

        // The stub in the app root survives updates, unlike the versioned files in current\
        var stub = Path.Combine(AppRoot, "LighthouseManager.Tray.exe");
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

    private static void RegisterTask(bool atLogon)
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
                  <Enabled>{(atLogon ? "true" : "false")}</Enabled>
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
