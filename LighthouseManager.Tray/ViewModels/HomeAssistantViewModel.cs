using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LighthouseManager.Tray.Api;

namespace LighthouseManager.Tray.ViewModels;

/// <summary>
///     "Home Assistant" settings page: MQTT broker connection of the service (<c>GET/PUT /api/mqtt</c>).
/// </summary>
public sealed partial class HomeAssistantViewModel(ApiClient api) : ObservableObject
{
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "1883";
    [ObservableProperty] private string _username = "";

    /// <summary>
    ///     New password; empty keeps the stored one (the service never returns it).
    /// </summary>
    [ObservableProperty] private string _password = "";

    [ObservableProperty] private bool _useTls;
    [ObservableProperty] private string _nodeId = "";
    [ObservableProperty] private string _discoveryPrefix = "homeassistant";
    [ObservableProperty] private string _baseTopic = "lighthousemanager";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PasswordPlaceholder))]
    private bool _hasPassword;

    [ObservableProperty] private string _defaultNodeId = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsConnected), nameof(HasProblem))]
    private MqttConfig? _status;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasProblem))]
    private string? _saveError;

    [ObservableProperty] private bool _isSaving;

    public string PasswordPlaceholder => HasPassword ? "Stored, leave empty to keep it" : "Password (optional)";

    public bool IsConnected => Status is { Enabled: true, Connected: true };

    public bool HasProblem => SaveError != null || Status is { Enabled: true, Connected: false, Error: not null };

    public string StatusText => SaveError ?? Status switch
    {
        null => "",
        { Enabled: false } => "Off",
        { Connected: true } => $"Connected to {Status.Host}:{Status.Port}",
        { Error: { } error } => $"Not connected: {error}",
        _ => "Connecting…"
    };

    /// <summary>
    ///     Loads the current settings from the service (also the connection status).
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            Apply(await api.GetMqttAsync(), loadFields: true);
        }
        catch (Exception ex)
        {
            SaveError = $"Can't read the settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        SaveError = null;
        if (!int.TryParse(Port.Trim(), out var port))
        {
            SaveError = "The port must be a number.";
            return;
        }

        IsSaving = true;
        try
        {
            var result = await api.SetMqttAsync(new MqttConfigRequest(Enabled, Host, port, Username,
                Password.Length > 0 ? Password : null, UseTls, DiscoveryPrefix, BaseTopic, NodeId));
            Password = "";
            Apply(result, loadFields: false);

            // Connecting takes a moment; show the outcome
            await Task.Delay(TimeSpan.FromSeconds(3));
            Apply(await api.GetMqttAsync(), loadFields: false);
        }
        catch (Exception ex)
        {
            SaveError = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Apply(MqttConfig config, bool loadFields)
    {
        Dispatcher.UIThread.VerifyAccess();
        Status = config;
        HasPassword = config.HasPassword;
        DefaultNodeId = config.DefaultNodeId;
        if (!loadFields) return;

        Enabled = config.Enabled;
        Host = config.Host;
        Port = config.Port.ToString();
        Username = config.Username;
        Password = "";
        UseTls = config.UseTls;
        NodeId = config.NodeId;
        DiscoveryPrefix = config.DiscoveryPrefix;
        BaseTopic = config.BaseTopic;
    }
}
