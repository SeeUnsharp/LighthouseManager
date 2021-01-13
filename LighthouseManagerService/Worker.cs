using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LighthouseManagerService
{
    public class Worker : BackgroundService
    {
        private static bool _started;
        private readonly ILogger<Worker> _logger;
        private readonly IOptions<AppSettings> _settings;


        public Worker(ILogger<Worker> logger, IOptions<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                WatchForSteamVrProcess();
                await Task.Delay(_settings.Value.Interval, stoppingToken);
            }
        }

        private void WatchForSteamVrProcess()
        {
            if (_started) return;

            var process = Process.GetProcessesByName("vrserver").SingleOrDefault();

            if (process == null) return;
            _started = true;

            process.EnableRaisingEvents = true;

            Console.WriteLine("SteamVR detected");
            Console.WriteLine("Starting LighthouseManager and start base stations");

            var lighthouseManagerProcess = Process.Start(_settings.Value.LighthouseManagerPath,
                $"-w -a {_settings.Value.BaseStationAddresses}");

            process.Exited += delegate
            {
                // If LighthouseManager is still trying to start base station (but SteamVR exited) it should just stop for next actions
                if (lighthouseManagerProcess != null && !lighthouseManagerProcess.HasExited)
                    lighthouseManagerProcess.Kill();

                Console.WriteLine("SteamVR closed");
                Console.WriteLine("Starting LighthouseManager and stopping base stations");
                Process.Start(_settings.Value.LighthouseManagerPath, $"-s -a {_settings.Value.BaseStationAddresses}");
                _started = false;
            };
        }
    }
}