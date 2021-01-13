using System.Diagnostics;
using System.IO;

namespace LighthouseManagerService
{
    public static class Helper
    {
        public static string GetBasePath()
        {
            using var processModule = Process.GetCurrentProcess().MainModule;
            return Path.GetDirectoryName(processModule?.FileName);
        }
    }
}