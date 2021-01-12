using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Timers;
using Microsoft.Extensions.Configuration;

namespace SteamVRWatcher
{
    internal class App
    {
        private static bool _started;
        private readonly IConfiguration _config;
        private Timer _timer;

        public App(IConfiguration config)
        {
            _config = config;
        }

        public void Run()
        {
            Console.CancelKeyPress += delegate
            {
                _timer.Stop();
                _timer.Dispose();
            };

            var interval = _config.GetValue<int>("Runtime:Interval");

            var lighthouseManagerPath = _config.GetValue<string>("Runtime:LighthouseManagerPath");
            if (File.Exists(lighthouseManagerPath))
            {
                _timer = new Timer(interval);
                _timer.Start();
                _timer.Elapsed += delegate { WatchForSteamVrProcess(_config, lighthouseManagerPath); };
                Console.WriteLine("Listening for SteamVR");
            }
            else
            {
                Console.WriteLine("LighthouseManager.exe not found");
            }
        }

        private static void WatchForSteamVrProcess(IConfiguration config, string lighthouseManagerPath)
        {
            if (_started) return;

            var process = Process.GetProcessesByName("vrserver").SingleOrDefault();

            if (process == null) return;
            _started = true;

            process.EnableRaisingEvents = true;

            Console.WriteLine("SteamVR detected");
            Console.WriteLine("Starting LighthouseManager and start base stations");

            var addresses = config.GetValue<string>("Runtime:BaseStationAddresses");
            var lighthouseManagerProcess = Process.Start(lighthouseManagerPath, $"-w -a {addresses}");

            process.Exited += delegate
            {
                // If LighthouseManager is still trying to start base station (but SteamVR exited) it should just stop for next actions
                if (lighthouseManagerProcess != null && !lighthouseManagerProcess.HasExited)
                {
                    lighthouseManagerProcess.Kill();
                }

                Console.WriteLine("SteamVR closed");
                Console.WriteLine("Starting LighthouseManager and stopping base stations");
                Process.Start(lighthouseManagerPath, $"-s -a {addresses}");
                _started = false;
            };
        }
    }
}