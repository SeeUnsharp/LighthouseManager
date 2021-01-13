using System;
using System.Diagnostics;
using System.IO;
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
        private string _lighthouseManagerPath;


        public Worker(ILogger<Worker> logger, IOptions<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _lighthouseManagerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LighthouseManager.exe");

            if (!File.Exists(_lighthouseManagerPath))
            {
                _logger.LogCritical(
                    $"{_lighthouseManagerPath} not found. Please check appsettings.json");
                return;
            }

            _logger.LogInformation("Start listening for SteamVR events.");
            while (!stoppingToken.IsCancellationRequested)
            {
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

            _logger.LogInformation("SteamVR detected");
            _logger.LogInformation(
                $"Starting LighthouseManager and wake base stations: {_settings.Value.BaseStationAddresses}");

            var lighthouseManagerProcess = Process.Start(_lighthouseManagerPath,
                $"-w -a {_settings.Value.BaseStationAddresses}");

            process.Exited += delegate
            {
                // If LighthouseManager is still trying to start base station (but SteamVR exited) it should just stop for next actions
                if (lighthouseManagerProcess != null && !lighthouseManagerProcess.HasExited)
                    lighthouseManagerProcess.Kill();

                _logger.LogInformation("SteamVR closed");
                _logger.LogInformation(
                    $"Starting LighthouseManager and sleep base stations: {_settings.Value.BaseStationAddresses}");
                lighthouseManagerProcess = Process.Start(_lighthouseManagerPath,
                    $"-s -a {_settings.Value.BaseStationAddresses}");

                _started = false;
            };
        }
    }
}