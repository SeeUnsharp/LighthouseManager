using System;
using System.Diagnostics;
using System.IO;

namespace LighthouseManager.Shared
{
    public static class Helper
    {
        public static string GetBasePath()
        {
            var processModule = Process.GetCurrentProcess().MainModule;
            return Path.GetDirectoryName(processModule?.FileName);
        }
    }
}
