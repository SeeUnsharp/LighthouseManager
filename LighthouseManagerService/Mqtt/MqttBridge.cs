using LighthouseManager.Core;
using MQTTnet;
using MQTTnet.Protocol;

namespace LighthouseManagerService.Mqtt;

/// <param name="Broker">"host:port" of the configured broker, or null if none is configured.</param>
public sealed record MqttStatus(bool Enabled, bool Connected, string? Broker, string? Error);

/// <summary>
///     Home Assistant integration: connects to an MQTT broker, announces this computer and its base stations via MQTT
///     discovery, publishes their states and executes commands through the <see cref="StationController" /> (like
///     the tray, so manual commands from Home Assistant win over automatic mode). Settings can change at runtime
///     (<see cref="Apply" />).
/// </summary>
public sealed class MqttBridge(ILogger<MqttBridge> logger, StationController controller) : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly Dictionary<string, string> _publishedConfigs = [];
    private readonly Dictionary<string, string> _publishedStates = [];
    private MqttSettings _settings = MqttSettingsStore.Load();
    private CancellationTokenSource _settingsChanged = new();
    private bool _connected;
    private string? _error;
    private CancellationToken _stopping;

    public MqttStatus Status
    {
        get
        {
            lock (_sync)
            {
                var broker = _settings.Host.Length > 0 ? $"{_settings.Host}:{_settings.Port}" : null;
                return new MqttStatus(_settings.Enabled, _connected, broker, _error);
            }
        }
    }

    public MqttSettings Settings
    {
        get
        {
            lock (_sync) return _settings;
        }
    }

    /// <summary>
    ///     Saves new settings and reconnects with them.
    /// </summary>
    public void Apply(MqttSettings settings)
    {
        MqttSettingsStore.Save(settings);
        CancellationTokenSource changed;
        lock (_sync)
        {
            _settings = settings;
            _error = null;
            changed = _settingsChanged;
            _settingsChanged = new CancellationTokenSource();
        }

        // Ends the running session (or the wait before the next attempt)
        changed.Cancel();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var backoff = MinBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            MqttSettings settings;
            CancellationToken changed;
            lock (_sync)
            {
                settings = _settings;
                changed = _settingsChanged.Token;
            }

            using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, changed);
            if (!settings.Enabled || settings.Validate() != null)
            {
                SetConnected(false, settings.Enabled ? settings.Validate() : null);
                await WaitAsync(Timeout.InfiniteTimeSpan, session.Token);
                continue;
            }

            try
            {
                await RunSessionAsync(settings, session.Token);
                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                // Stopping or settings changed: reconnect right away with the new settings
                continue;
            }
            catch (Exception ex)
            {
                logger.LogWarning("MQTT: {Message}. Retrying in {Seconds} s.", ex.Message, backoff.TotalSeconds);
                SetConnected(false, ex.Message);
            }

            await WaitAsync(backoff, session.Token);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
        }
    }

    private async Task RunSessionAsync(MqttSettings settings, CancellationToken ct)
    {
        var ha = new HomeAssistantMqtt(settings, AppPaths.Version);
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();

        var builder = factory.CreateClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId($"lighthousemanager-{ha.NodeId}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithWillTopic(ha.AvailabilityTopic)
            .WithWillPayload(HomeAssistantMqtt.Offline)
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (settings.Username.Length > 0) builder = builder.WithCredentials(settings.Username, settings.Password);
        if (settings.UseTls) builder = builder.WithTlsOptions(o => o.UseTls());

        // A dropped connection ends the session; the main loop reconnects
        using var lost = new CancellationTokenSource();
        client.DisconnectedAsync += _ =>
        {
            lost.Cancel();
            return Task.CompletedTask;
        };
        var homeAssistantStatus = $"{settings.DiscoveryPrefix}/status";
        client.ApplicationMessageReceivedAsync += e =>
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
            if (topic == homeAssistantStatus)
            {
                // Home Assistant restarted: announce everything again
                if (payload == HomeAssistantMqtt.Online) RepublishAll(client, ha);
            }
            else if (ha.ParseCommand(topic, payload) is { } command)
            {
                _ = ExecuteAsync(command);
            }

            return Task.CompletedTask;
        };

        logger.LogInformation("MQTT: connecting to {Host}:{Port}", settings.Host, settings.Port);
        var result = await client.ConnectAsync(builder.Build(), ct);
        // A rejected login isn't an exception in MQTTnet, only the result code says so
        if (result.ResultCode != MqttClientConnectResultCode.Success)
            throw new IOException(result.ResultCode switch
            {
                MqttClientConnectResultCode.BadUserNameOrPassword => "Wrong username or password",
                MqttClientConnectResultCode.NotAuthorized => "Not authorized: check username and password",
                MqttClientConnectResultCode.ServerUnavailable or MqttClientConnectResultCode.ServerBusy =>
                    "The broker is not available",
                MqttClientConnectResultCode.Banned => "This client is banned by the broker",
                _ => $"The broker rejected the connection ({result.ResultCode})"
            });
        await client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(ha.CommandFilter).WithAtLeastOnceQoS())
            .WithTopicFilter(f => f.WithTopic(homeAssistantStatus).WithAtLeastOnceQoS())
            .Build(), ct);
        lock (_sync)
        {
            _publishedConfigs.Clear();
            _publishedStates.Clear();
        }

        await PublishAsync(client, new MqttMessage(ha.AvailabilityTopic, HomeAssistantMqtt.Online), ct);
        SetConnected(true, null);
        logger.LogInformation("MQTT: connected, announcing to Home Assistant as \"{NodeId}\"", ha.NodeId);

        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, lost.Token);
        try
        {
            await foreach (var snapshot in controller.SubscribeAsync(run.Token))
            {
                await PublishDiscoveryAsync(client, ha, snapshot, run.Token);
                await PublishChangedAsync(client, ha.StateMessages(snapshot), _publishedStates, run.Token);
            }
        }
        catch (OperationCanceledException) when (lost.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new IOException("Connection to the broker lost");
        }
        finally
        {
            await EndSessionAsync(client, ha, settings);
        }
    }

    private async Task PublishDiscoveryAsync(IMqttClient client, HomeAssistantMqtt ha, StatusSnapshot snapshot,
        CancellationToken ct)
    {
        var messages = ha.DiscoveryMessages(snapshot.Stations);
        await PublishChangedAsync(client, messages, _publishedConfigs, ct);

        // Removed base stations: an empty retained config removes their entities from Home Assistant
        var current = messages.Select(m => m.Topic).ToHashSet();
        List<string> removed;
        lock (_sync) removed = _publishedConfigs.Keys.Where(t => !current.Contains(t)).ToList();
        foreach (var topic in removed)
        {
            await PublishAsync(client, new MqttMessage(topic, ""), ct);
            lock (_sync) _publishedConfigs.Remove(topic);
        }
    }

    private async Task PublishChangedAsync(IMqttClient client, IEnumerable<MqttMessage> messages,
        Dictionary<string, string> published, CancellationToken ct)
    {
        foreach (var message in messages)
        {
            lock (_sync)
            {
                if (published.TryGetValue(message.Topic, out var previous) && previous == message.Payload) continue;
            }

            await PublishAsync(client, message, ct);
            lock (_sync) published[message.Topic] = message.Payload;
        }
    }

    private void RepublishAll(IMqttClient client, HomeAssistantMqtt ha)
    {
        lock (_sync)
        {
            _publishedConfigs.Clear();
            _publishedStates.Clear();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await PublishAsync(client, new MqttMessage(ha.AvailabilityTopic, HomeAssistantMqtt.Online), _stopping);
                var snapshot = controller.GetSnapshot();
                await PublishDiscoveryAsync(client, ha, snapshot, _stopping);
                await PublishChangedAsync(client, ha.StateMessages(snapshot), _publishedStates, _stopping);
            }
            catch (Exception ex)
            {
                logger.LogDebug("MQTT: republishing failed: {Message}", ex.Message);
            }
        });
    }

    /// <summary>
    ///     Goes offline. If the topics change or the integration is turned off, the entities are removed from Home
    ///     Assistant first, so nothing is left behind there.
    /// </summary>
    private async Task EndSessionAsync(IMqttClient client, HomeAssistantMqtt ha, MqttSettings sessionSettings)
    {
        SetConnected(false, Status.Error);
        if (!client.IsConnected) return;

        var next = Settings;
        var identityChanged = !next.Enabled || next.DiscoveryPrefix != sessionSettings.DiscoveryPrefix ||
                              next.BaseTopic != sessionSettings.BaseTopic ||
                              next.EffectiveNodeId != sessionSettings.EffectiveNodeId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (identityChanged)
            {
                // Empty retained messages delete the configs (Home Assistant removes the entities) and the old
                // states, so the broker doesn't keep stale topics
                List<string> topics;
                lock (_sync) topics = _publishedConfigs.Keys.Concat(_publishedStates.Keys).ToList();
                foreach (var topic in topics) await PublishAsync(client, new MqttMessage(topic, ""), timeout.Token);
                lock (_sync)
                {
                    _publishedConfigs.Clear();
                    _publishedStates.Clear();
                }

                await PublishAsync(client, new MqttMessage(ha.AvailabilityTopic, ""), timeout.Token);
            }
            else
            {
                await PublishAsync(client, new MqttMessage(ha.AvailabilityTopic, HomeAssistantMqtt.Offline),
                    timeout.Token);
            }

            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token);
            logger.LogInformation("MQTT: disconnected");
        }
        catch (Exception ex)
        {
            logger.LogDebug("MQTT: disconnecting failed: {Message}", ex.Message);
        }
    }

    private async Task ExecuteAsync(MqttCommand command)
    {
        try
        {
            switch (command)
            {
                case MqttCommand.SetAutomaticMode(var enabled):
                    logger.LogInformation("Home Assistant: automatic mode {State}", enabled ? "on" : "off");
                    controller.AutomaticMode = enabled;
                    break;
                case MqttCommand.SetAllPower(var state):
                    logger.LogInformation("Home Assistant: {State} all base stations", state.ToString());
                    await controller.SetPowerAsync(controller.Addresses, state, false, _stopping);
                    break;
                case MqttCommand.SetStationPower(var address, var state) when controller.IsKnown(address):
                    logger.LogInformation("Home Assistant: {State} {Address}", state.ToString(),
                        MacAddress.Format(address));
                    await controller.SetPowerAsync([address], state, false, _stopping);
                    break;
                case MqttCommand.Identify(var address) when controller.IsKnown(address):
                    logger.LogInformation("Home Assistant: identify {Address}", MacAddress.Format(address));
                    await controller.IdentifyAsync(address, _stopping);
                    break;
                case MqttCommand.Refresh:
                    await controller.RefreshAsync(null, _stopping);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Service stopping
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Home Assistant command {Command} failed", command);
        }
    }

    private static Task PublishAsync(IMqttClient client, MqttMessage message, CancellationToken ct) =>
        client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(message.Topic)
            .WithPayload(message.Payload)
            .WithRetainFlag(message.Retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

    private void SetConnected(bool connected, string? error)
    {
        lock (_sync)
        {
            _connected = connected;
            _error = error;
        }
    }

    private static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
