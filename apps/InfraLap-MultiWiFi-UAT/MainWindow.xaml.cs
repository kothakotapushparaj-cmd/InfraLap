using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.Win32;

namespace InfraLap.MultiWiFi;

public partial class MainWindow : Window
{
    private sealed record WifiInterface(
        string Name, string State, string Description, string Ssid, string InterfaceGuid,
        string MacAddress, string PnpDeviceId, string Manufacturer, int SignalPercent,
        int Channel, string RadioType, string Bssid, string ReceiveRate, string TransmitRate)
    {
        public string IdentityKey =>
            !string.IsNullOrWhiteSpace(PnpDeviceId) ? PnpDeviceId :
            !string.IsNullOrWhiteSpace(MacAddress) ? MacAddress + "|" + Description :
            !string.IsNullOrWhiteSpace(InterfaceGuid) ? InterfaceGuid : Name + "|" + Description;
        public string DisplayText => string.IsNullOrWhiteSpace(Description)
            ? Name + " — " + State : Name + " — " + Description + " — " + State;
    }

    private sealed record HardwareIdentity(
        string InterfaceName, string AdapterName, string Manufacturer, string MacAddress,
        string PnpDeviceId, string InterfaceGuid, bool PhysicalAdapter);

    private sealed class WifiNetwork
    {
        public string Ssid { get; init; } = "";
        public string Authentication { get; set; } = "";
        public string Encryption { get; set; } = "";
        public int SignalPercent { get; set; } = -1;
        public string RadioType { get; set; } = "";
        public int Channel { get; set; } = -1;
        public string DetailText
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Authentication)) parts.Add(Authentication);
                if (SignalPercent >= 0) parts.Add(SignalPercent + "% signal");
                if (Channel >= 0) parts.Add("Channel " + Channel);
                if (!string.IsNullOrWhiteSpace(RadioType)) parts.Add(RadioType);
                return parts.Count == 0 ? "Network details unavailable" : string.Join(" · ", parts);
            }
        }
        public override string ToString() => Ssid;
    }

    private sealed record NetshResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => string.Join(Environment.NewLine,
            new[] { StandardOutput, StandardError }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private sealed class AdapterCard
    {
        public WifiInterface Adapter { get; set; } = null!;
        public Border Root { get; set; } = null!;
        public TextBlock Heading { get; set; } = null!;
        public TextBlock InterfaceLine { get; set; } = null!;
        public TextBlock ConnectionBadge { get; set; } = null!;
        public TextBlock Status { get; set; } = null!;
        public TextBlock SignalLabel { get; set; } = null!;
        public ProgressBar SignalBar { get; set; } = null!;
        public TextBlock IdentityDetails { get; set; } = null!;
        public TextBox Nickname { get; set; } = null!;
        public ComboBox NetworkBox { get; set; } = null!;
        public PasswordBox PasswordInput { get; set; } = null!;
        public TextBox PlainPasswordInput { get; set; } = null!;
        public Button TogglePasswordButton { get; set; } = null!;
        public Button ConnectButton { get; set; } = null!;
        public Button DisconnectButton { get; set; } = null!;
        public bool IsBusy { get; set; }
        public string TargetSsid => NetworkBox.SelectedItem is WifiNetwork item ? item.Ssid : NetworkBox.Text.Trim();
        public string Password => PlainPasswordInput.Visibility == Visibility.Visible
            ? PlainPasswordInput.Text : PasswordInput.Password;
    }

    private readonly List<WifiInterface> _interfaces = new();
    private readonly List<WifiNetwork> _scannedNetworks = new();
    private List<SavedWifiProfile> _savedProfiles = new();
    private SavedWifiProfile? _selectedSavedProfile;
    private readonly List<AdapterCard> _adapterCards = new();
    private Dictionary<string, string> _adapterLabels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Brush MutedBrush = BrushFrom("#91A3BB");
    private static readonly Brush GreenBrush = BrushFrom("#54D6A2");
    private static readonly Brush AmberBrush = BrushFrom("#F0B86A");
    private static readonly Brush RedBrush = BrushFrom("#F07D88");
    private static readonly Brush BlueBrush = BrushFrom("#80B1FF");

    public MainWindow()
    {
        InitializeComponent();
        LoadAdapterLabels();
        Loaded += async (_, _) => await RefreshAllAsync();
        Closed += (_, _) =>
        {
            foreach (var card in _adapterCards)
            {
                card.PasswordInput.Clear();
                card.PlainPasswordInput.Clear();
            }
            SavedPasswordHidden.Clear();
            SavedPasswordVisible.Clear();
            NativeWifiService.ReleaseDualSta();
        };
    }

    private static Brush BrushFrom(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanNetworksAsync();
    private async void SavedProfiles_Click(object sender, RoutedEventArgs e) => await RefreshSavedProfilesAsync();
    private void SavedProfilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => SelectSavedProfile();
    private void ToggleSavedPassword_Click(object sender, RoutedEventArgs e) => ToggleSavedPassword();
    private void UseSavedProfile_Click(object sender, RoutedEventArgs e) => UseSavedProfileOnTargetAdapter();
    private async void Export_Click(object sender, RoutedEventArgs e) => await ExportDiagnosticsAsync();

    private async Task RefreshAllAsync()
    {
        StatusText.Text = "Discovering Windows wireless adapters and hardware identities...";
        try
        {
            var snapshots = _adapterCards.ToDictionary(
                x => x.Adapter.IdentityKey,
                x => new CardSnapshot(x.TargetSsid, x.Nickname.Text),
                StringComparer.OrdinalIgnoreCase);

            var detected = await Task.Run(ReadAdapters);
            _interfaces.Clear();
            _interfaces.AddRange(detected);
            BuildAdapterCards(snapshots);

            SavedTargetAdapter.ItemsSource = _interfaces;
            if (_interfaces.Count > 0)
            {
                var savedMatch = _selectedSavedProfile is null ? null : _interfaces.FirstOrDefault(x =>
                    Guid.TryParse(NormalizeGuid(x.InterfaceGuid), out var id) && id == _selectedSavedProfile.AdapterGuid);
                SavedTargetAdapter.SelectedItem = savedMatch ?? _interfaces[0];
            }
            else SavedTargetAdapter.SelectedItem = null;

            var dualSta = await Task.Run(NativeWifiService.GetDualStaSummary);
            HardwareStatus.Text = BuildHardwareSummary() + Environment.NewLine + Environment.NewLine
                + "Driver Dual-STA report: " + dualSta;

            await ScanNetworksAsync(showStatus: false);
            await RefreshSavedProfilesAsync(showStatus: false);
            UpdateSummaryCounts();
            StatusText.Text = "Refresh complete. " + _interfaces.Count + " Wi-Fi interface(s) detected.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Adapter refresh failed: " + ex.Message;
            HardwareStatus.Text = "Could not enumerate WLAN adapters. Check that the Windows WLAN service is running and drivers are installed.";
            MessageBox.Show(ex.Message, "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed record CardSnapshot(string Ssid, string Nickname);

    private void BuildAdapterCards(IReadOnlyDictionary<string, CardSnapshot>? snapshots = null)
    {
        _adapterCards.Clear();
        AdapterCardsPanel.Children.Clear();
        var index = 0;
        foreach (var adapter in _interfaces)
        {
            index++;
            var card = CreateAdapterCard(adapter, index);
            if (snapshots is not null && snapshots.TryGetValue(adapter.IdentityKey, out var snapshot))
            {
                SetCardSsid(card, snapshot.Ssid);
                card.Nickname.Text = snapshot.Nickname;
            }
            _adapterCards.Add(card);
            AdapterCardsPanel.Children.Add(card.Root);
            UpdateCardReadback(card);
        }
        AdapterEmptyText.Visibility = _interfaces.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummaryCounts();
    }

    private AdapterCard CreateAdapterCard(WifiInterface adapter, int index)
    {
        var card = new AdapterCard { Adapter = adapter };
        var root = new Border
        {
            Width = 520, Margin = new Thickness(6), Padding = new Thickness(15),
            CornerRadius = new CornerRadius(12), Background = BrushFrom("#142238"),
            BorderBrush = BrushFrom("#304866"), BorderThickness = new Thickness(1)
        };
        var panel = new StackPanel();
        root.Child = panel;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingStack = new StackPanel();
        headingStack.Children.Add(new TextBlock
        {
            Text = "WIRELESS ADAPTER " + index.ToString("00", CultureInfo.InvariantCulture),
            Foreground = BlueBrush, FontSize = 8, FontWeight = FontWeights.Bold
        });
        var heading = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(adapter.Description) ? adapter.Name : adapter.Description,
            FontSize = 16, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 8, 0)
        };
        var interfaceLine = new TextBlock
        {
            Text = "Windows interface: " + adapter.Name,
            Foreground = MutedBrush, FontSize = 9, Margin = new Thickness(0, 3, 0, 0)
        };
        headingStack.Children.Add(heading);
        headingStack.Children.Add(interfaceLine);
        header.Children.Add(headingStack);

        var badge = new TextBlock
        {
            Text = adapter.State.ToUpperInvariant(), Foreground = GreenBrush,
            Background = BrushFrom("#18392F"), FontSize = 8, FontWeight = FontWeights.Bold,
            Padding = new Thickness(9, 6, 9, 6), VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);
        panel.Children.Add(header);

        panel.Children.Add(MakeLabel("OPTIONAL ADAPTER LABEL"));
        var nickname = new TextBox
        {
            Height = 34, Margin = new Thickness(0, 4, 0, 10),
            Text = _adapterLabels.TryGetValue(adapter.IdentityKey, out var savedLabel) ? savedLabel : "",
            ToolTip = "Optional name such as Office Wi-Fi or Backup USB"
        };
        nickname.LostFocus += (_, _) => SaveAdapterLabel(card);
        nickname.TextChanged += (_, _) =>
        {
            if (card.Heading is not null)
                card.Heading.Text = string.IsNullOrWhiteSpace(nickname.Text)
                    ? (string.IsNullOrWhiteSpace(card.Adapter.Description) ? card.Adapter.Name : card.Adapter.Description)
                    : nickname.Text.Trim();
        };
        panel.Children.Add(nickname);

        panel.Children.Add(MakeLabel("CHOOSE NETWORK / SSID"));
        var networkBox = new ComboBox
        {
            Height = 40, Margin = new Thickness(0, 4, 0, 10), IsEditable = true,
            IsTextSearchEnabled = true, StaysOpenOnEdit = false,
            DisplayMemberPath = nameof(WifiNetwork.Ssid),
            ItemTemplate = (DataTemplate)FindResource("NetworkItemTemplate"),
            ItemsSource = _scannedNetworks, ToolTip = "Select a scanned network or type a hidden SSID"
        };
        TextSearch.SetTextPath(networkBox, nameof(WifiNetwork.Ssid));
        networkBox.SelectionChanged += (_, _) => UpdateCardReadback(card);
        panel.Children.Add(networkBox);

        panel.Children.Add(MakeLabel("WPA2-PERSONAL PASSWORD"));
        var passwordGrid = new Grid { Margin = new Thickness(0, 4, 0, 5) };
        var passwordBox = new PasswordBox { Height = 36 };
        var plainPassword = new TextBox { Height = 36, Visibility = Visibility.Collapsed };
        passwordGrid.Children.Add(passwordBox);
        passwordGrid.Children.Add(plainPassword);
        panel.Children.Add(passwordGrid);

        var actionRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 9) };
        var toggleButton = new Button
        {
            Content = "Show password", Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(0, 0, 7, 0), Padding = new Thickness(10, 7, 10, 7)
        };
        var connectButton = new Button { Content = "Connect", MinWidth = 82 };
        var disconnectButton = new Button
        {
            Content = "Disconnect", MinWidth = 86, Style = (Style)FindResource("DangerButton"),
            Margin = new Thickness(7, 0, 0, 0)
        };
        actionRow.Children.Add(toggleButton);
        actionRow.Children.Add(connectButton);
        actionRow.Children.Add(disconnectButton);
        panel.Children.Add(actionRow);

        var signalPanel = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        signalPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        signalPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var signalLabel = new TextBlock
        {
            Text = "Signal unavailable", Foreground = MutedBrush, FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        var signalBar = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Height = 7, VerticalAlignment = VerticalAlignment.Center,
            Background = BrushFrom("#263952"), Foreground = GreenBrush
        };
        Grid.SetColumn(signalBar, 1);
        signalPanel.Children.Add(signalLabel);
        signalPanel.Children.Add(signalBar);
        panel.Children.Add(signalPanel);

        var statusBorder = new Border
        {
            Background = BrushFrom("#0B1728"), BorderBrush = BrushFrom("#293D59"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(10)
        };
        var status = new TextBlock
        {
            Text = "Waiting for connection details...", Foreground = MutedBrush,
            FontSize = 9, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None
        };
        statusBorder.Child = status;
        panel.Children.Add(statusBorder);

        var expander = new Expander
        {
            Header = "Hardware identity · MAC · GUID · Plug-and-Play ID",
            Foreground = BrushFrom("#C4D5EA"), FontSize = 9, Margin = new Thickness(0, 10, 0, 0)
        };
        var identityDetails = new TextBlock
        {
            Foreground = MutedBrush, FontFamily = new FontFamily("Consolas"),
            FontSize = 8, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
            Margin = new Thickness(0, 7, 0, 4)
        };
        expander.Content = identityDetails;
        panel.Children.Add(expander);

        card.Root = root;
        card.Heading = heading;
        card.InterfaceLine = interfaceLine;
        card.ConnectionBadge = badge;
        card.Status = status;
        card.SignalLabel = signalLabel;
        card.SignalBar = signalBar;
        card.IdentityDetails = identityDetails;
        card.Nickname = nickname;
        card.NetworkBox = networkBox;
        card.PasswordInput = passwordBox;
        card.PlainPasswordInput = plainPassword;
        card.TogglePasswordButton = toggleButton;
        card.ConnectButton = connectButton;
        card.DisconnectButton = disconnectButton;

        toggleButton.Click += (_, _) => ToggleCardPassword(card);
        connectButton.Click += async (_, _) => await ConnectProfileAsync(card);
        disconnectButton.Click += async (_, _) => await DisconnectAdapterAsync(card);
        UpdateAdapterIdentity(card);
        return card;
    }

    private static TextBlock MakeLabel(string text) => new()
    {
        Text = text, Foreground = MutedBrush, FontSize = 8, FontWeight = FontWeights.Bold
    };

    private void SaveAdapterLabel(AdapterCard card)
    {
        var label = card.Nickname.Text.Trim();
        if (string.IsNullOrWhiteSpace(label)) _adapterLabels.Remove(card.Adapter.IdentityKey);
        else _adapterLabels[card.Adapter.IdentityKey] = label;
        SaveAdapterLabels();
    }

    private void LoadAdapterLabels()
    {
        try
        {
            if (File.Exists(AdapterLabelsPath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AdapterLabelsPath));
                if (loaded is not null) _adapterLabels = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { _adapterLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveAdapterLabels()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AdapterLabelsPath)!);
            File.WriteAllText(AdapterLabelsPath,
                JsonSerializer.Serialize(_adapterLabels, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { StatusText.Text = "Could not save adapter labels: " + ex.Message; }
    }

    private static string AdapterLabelsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "InfraLap", "MultiWiFi", "adapter-labels.json");

    private void ToggleCardPassword(AdapterCard card)
    {
        if (card.PlainPasswordInput.Visibility == Visibility.Visible)
        {
            card.PasswordInput.Password = card.PlainPasswordInput.Text;
            card.PlainPasswordInput.Visibility = Visibility.Collapsed;
            card.PasswordInput.Visibility = Visibility.Visible;
            card.TogglePasswordButton.Content = "Show password";
        }
        else
        {
            card.PlainPasswordInput.Text = card.PasswordInput.Password;
            card.PasswordInput.Visibility = Visibility.Collapsed;
            card.PlainPasswordInput.Visibility = Visibility.Visible;
            card.TogglePasswordButton.Content = "Hide password";
        }
    }

    private static void ClearCardPassword(AdapterCard card)
    {
        card.PasswordInput.Clear();
        card.PlainPasswordInput.Clear();
        card.PasswordInput.Visibility = Visibility.Visible;
        card.PlainPasswordInput.Visibility = Visibility.Collapsed;
        card.TogglePasswordButton.Content = "Show password";
    }

    private void SetCardSsid(AdapterCard card, string? ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            card.NetworkBox.SelectedItem = null;
            card.NetworkBox.Text = "";
            return;
        }
        var match = _scannedNetworks.FirstOrDefault(x => string.Equals(x.Ssid, ssid, StringComparison.Ordinal));
        if (match is not null) card.NetworkBox.SelectedItem = match;
        else { card.NetworkBox.SelectedItem = null; card.NetworkBox.Text = ssid; }
    }

    private async Task ScanNetworksAsync(bool showStatus = true)
    {
        try
        {
            var previous = _adapterCards.ToDictionary(x => x.Adapter.IdentityKey, x => x.TargetSsid,
                StringComparer.OrdinalIgnoreCase);
            if (showStatus) StatusText.Text = "Scanning nearby Wi-Fi networks...";
            var result = await Task.Run(() => RunNetsh("wlan", "show", "networks", "mode=bssid"));
            EnsureNetshSuccess(result, "Scan nearby networks");
            NetworksOutput.Text = result.StandardOutput;
            _scannedNetworks.Clear();
            _scannedNetworks.AddRange(ParseScannedNetworks(result.StandardOutput));
            foreach (var card in _adapterCards)
            {
                card.NetworkBox.ItemsSource = null;
                card.NetworkBox.ItemsSource = _scannedNetworks;
                if (previous.TryGetValue(card.Adapter.IdentityKey, out var ssid)) SetCardSsid(card, ssid);
            }
            if (showStatus)
                StatusText.Text = "Scan complete: " + _scannedNetworks.Count + " unique SSID(s) found. Select a network on an adapter card.";
        }
        catch (Exception ex)
        {
            NetworksOutput.Text = ex.Message;
            if (showStatus) StatusText.Text = "Wi-Fi scan failed: " + ex.Message;
        }
    }

    private static List<WifiNetwork> ParseScannedNetworks(string output)
    {
        var networks = new Dictionary<string, WifiNetwork>(StringComparer.Ordinal);
        WifiNetwork? current = null;
        foreach (var raw in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.StartsWith("SSID", StringComparison.OrdinalIgnoreCase)
                && key.Length > 4 && char.IsDigit(key.Skip(4).FirstOrDefault(c => !char.IsWhiteSpace(c))))
            {
                current = null;
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!networks.TryGetValue(value, out current))
                {
                    current = new WifiNetwork { Ssid = value };
                    networks.Add(value, current);
                }
                continue;
            }
            if (current is null) continue;
            if (key.Equals("Authentication", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(current.Authentication)) current.Authentication = value;
            }
            else if (key.Equals("Encryption", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(current.Encryption)) current.Encryption = value;
            }
            else if (key.Equals("Signal", StringComparison.OrdinalIgnoreCase))
            {
                var numeric = value.Trim().TrimEnd('%');
                if (int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signal))
                    current.SignalPercent = Math.Max(current.SignalPercent, signal);
            }
            else if (key.Equals("Radio type", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(current.RadioType)) current.RadioType = value;
            }
            else if (key.Equals("Channel", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)
                    && current.Channel < 0) current.Channel = channel;
            }
        }
        return networks.Values.OrderByDescending(x => x.SignalPercent)
            .ThenBy(x => x.Ssid, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<WifiInterface> ReadAdapters()
    {
        var interfacesResult = RunNetsh("wlan", "show", "interfaces");
        EnsureNetshSuccess(interfacesResult, "Read WLAN interfaces");
        var interfaces = ParseInterfaces(interfacesResult.StandardOutput);
        var hardware = ReadHardwareIdentities();

        return interfaces.Select(wlan =>
        {
            var info = hardware.FirstOrDefault(x =>
                string.Equals(x.InterfaceName, wlan.Name, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(wlan.InterfaceGuid)
                    && string.Equals(NormalizeGuid(x.InterfaceGuid), NormalizeGuid(wlan.InterfaceGuid), StringComparison.OrdinalIgnoreCase)));
            if (info is null) return wlan;
            return wlan with
            {
                MacAddress = string.IsNullOrWhiteSpace(wlan.MacAddress) ? info.MacAddress : wlan.MacAddress,
                PnpDeviceId = info.PnpDeviceId,
                Manufacturer = info.Manufacturer,
                InterfaceGuid = string.IsNullOrWhiteSpace(wlan.InterfaceGuid) ? info.InterfaceGuid : wlan.InterfaceGuid
            };
        }).ToList();
    }

    private static List<HardwareIdentity> ReadHardwareIdentities()
    {
        const string script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; "
            + "$rows=Get-CimInstance -ClassName Win32_NetworkAdapter | Where-Object { $_.NetConnectionID -and $_.GUID } | "
            + "Select-Object @{Name='InterfaceName';Expression={$_.NetConnectionID}},@{Name='AdapterName';Expression={$_.Name}},"
            + "Manufacturer,MACAddress,PNPDeviceID,GUID,PhysicalAdapter; "
            + "if ($rows) { $rows | ConvertTo-Json -Depth 3 -Compress }";
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe", UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                }
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(script);
            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return new List<HardwareIdentity>();
            using var document = JsonDocument.Parse(stdout.Trim());
            var items = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { document.RootElement };
            return items.Select(item => new HardwareIdentity(
                ReadJsonString(item, "InterfaceName"), ReadJsonString(item, "AdapterName"),
                ReadJsonString(item, "Manufacturer"), ReadJsonString(item, "MACAddress"),
                ReadJsonString(item, "PNPDeviceID"), ReadJsonString(item, "GUID"),
                !item.TryGetProperty("PhysicalAdapter", out var physical) || physical.ValueKind != JsonValueKind.False
            )).ToList();
        }
        catch { return new List<HardwareIdentity>(); }
    }

    private static string ReadJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString() : "";

    private static string NormalizeGuid(string value) => value.Trim().Trim('{', '}');

    private string BuildHardwareSummary()
    {
        if (_interfaces.Count == 0) return "No WLAN interfaces currently reported by Windows.";
        var connected = _interfaces.Count(x => x.State.Equals("connected", StringComparison.OrdinalIgnoreCase));
        var mapped = _interfaces.Count(x => !string.IsNullOrWhiteSpace(x.PnpDeviceId));
        return _interfaces.Count + " WLAN interface(s) detected." + Environment.NewLine
            + connected + " currently connected." + Environment.NewLine
            + mapped + " interface(s) mapped to a Plug-and-Play hardware identity." + Environment.NewLine
            + "Each interface is managed independently; a supported USB adapter should appear as another card after driver installation.";
    }

    private void UpdateAdapterIdentity(AdapterCard card)
    {
        var adapter = card.Adapter;
        card.InterfaceLine.Text = "Windows interface: " + adapter.Name
            + (string.IsNullOrWhiteSpace(adapter.Manufacturer) ? "" : " · " + adapter.Manufacturer);
        card.IdentityDetails.Text =
            "Adapter description: " + adapter.Description + Environment.NewLine
            + "Manufacturer: " + (string.IsNullOrWhiteSpace(adapter.Manufacturer) ? "Not reported" : adapter.Manufacturer) + Environment.NewLine
            + "MAC address: " + (string.IsNullOrWhiteSpace(adapter.MacAddress) ? "Not reported" : adapter.MacAddress) + Environment.NewLine
            + "WLAN interface GUID: " + (string.IsNullOrWhiteSpace(adapter.InterfaceGuid) ? "Not reported" : adapter.InterfaceGuid) + Environment.NewLine
            + "Plug-and-Play device ID: " + (string.IsNullOrWhiteSpace(adapter.PnpDeviceId) ? "Not reported by Windows" : adapter.PnpDeviceId);
    }

    private void UpdateCardReadback(AdapterCard card)
    {
        var adapter = card.Adapter;
        card.ConnectionBadge.Text = adapter.State.ToUpperInvariant();
        var connected = adapter.State.Equals("connected", StringComparison.OrdinalIgnoreCase);
        card.ConnectionBadge.Foreground = connected ? GreenBrush : AmberBrush;
        card.ConnectionBadge.Background = BrushFrom(connected ? "#18392F" : "#3B3020");
        card.SignalBar.Value = adapter.SignalPercent < 0 ? 0 : Math.Clamp(adapter.SignalPercent, 0, 100);
        card.SignalLabel.Text = adapter.SignalPercent >= 0 ? "Signal " + adapter.SignalPercent + "%" : "Signal unavailable";

        var target = card.TargetSsid;
        if (connected)
        {
            if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(adapter.Ssid)
                && !string.Equals(target, adapter.Ssid, StringComparison.Ordinal))
                SetCardStatus(card, "Connected to '" + adapter.Ssid + "', not selected target '" + target + "'.", AmberBrush);
            else
                SetCardStatus(card, "Connected to '" + (string.IsNullOrWhiteSpace(adapter.Ssid) ? "SSID not reported" : adapter.Ssid) + "'."
                    + (string.IsNullOrWhiteSpace(adapter.ReceiveRate) ? "" : " Receive " + adapter.ReceiveRate + " Mbps.")
                    + (string.IsNullOrWhiteSpace(adapter.TransmitRate) ? "" : " Transmit " + adapter.TransmitRate + " Mbps."), GreenBrush);
        }
        else SetCardStatus(card, "Adapter state: " + adapter.State + ". Select a network and connect.", MutedBrush);
        UpdateAdapterIdentity(card);
    }

    private static void SetCardStatus(AdapterCard card, string message, Brush color)
    {
        card.Status.Text = message;
        card.Status.Foreground = color;
    }

    private void UpdateSummaryCounts()
    {
        AdapterCountText.Text = _interfaces.Count.ToString(CultureInfo.InvariantCulture);
        ConnectedCountText.Text = _interfaces.Count(x => x.State.Equals("connected", StringComparison.OrdinalIgnoreCase))
            .ToString(CultureInfo.InvariantCulture);
        SavedProfilesCountText.Text = _savedProfiles.Count.ToString(CultureInfo.InvariantCulture);
    }

    private async Task ConnectProfileAsync(AdapterCard card)
    {
        if (card.IsBusy) return;
        var adapter = card.Adapter;
        var ssid = card.TargetSsid;
        var password = card.Password;

        if (string.IsNullOrWhiteSpace(ssid))
        {
            SetCardStatus(card, "Enter a Wi-Fi network name (SSID).", AmberBrush);
            return;
        }
        if (Encoding.UTF8.GetByteCount(ssid) > 32)
        {
            SetCardStatus(card, "SSID is too long. Wi-Fi names are limited to 32 UTF-8 bytes.", RedBrush);
            return;
        }
        if (password.Length < 8 || password.Length > 63)
        {
            SetCardStatus(card, "For this WPA2-Personal editor, enter 8–63 password characters.", AmberBrush);
            return;
        }
        if (adapter.State.Equals("connected", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(adapter.Ssid) && !string.Equals(adapter.Ssid, ssid, StringComparison.Ordinal))
        {
            var confirm = MessageBox.Show(
                "This adapter is currently connected to '" + adapter.Ssid + "'. Connecting it to '" + ssid
                + "' will switch this adapter's network. Other Wi-Fi adapters will not be changed. Continue?",
                "Switch this adapter's Wi-Fi network", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        card.IsBusy = true;
        card.ConnectButton.IsEnabled = false;
        card.DisconnectButton.IsEnabled = false;
        SetCardStatus(card, "Adding this WPA2 profile to Windows and requesting connection...", BlueBrush);
        StatusText.Text = "Connecting " + adapter.Name + " to '" + ssid + "'...";
        try
        {
            var profileName = BuildProfileName(adapter);
            await Task.Run(() => AddProfileAndConnect(profileName, adapter.Name, ssid, password));
            ClearCardPassword(card);
            var verified = false;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(1500);
                var latest = await Task.Run(ReadNetshInterfaces);
                var current = latest.FirstOrDefault(x => string.Equals(x.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));
                if (current is null) continue;

                card.Adapter = adapter with
                {
                    State = current.State, Ssid = current.Ssid, SignalPercent = current.SignalPercent,
                    Channel = current.Channel, RadioType = current.RadioType, Bssid = current.Bssid,
                    ReceiveRate = current.ReceiveRate, TransmitRate = current.TransmitRate
                };
                ReplaceInterfaceSnapshot(card.Adapter);
                UpdateCardReadback(card);

                if (current.State.Equals("connected", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(current.Ssid, ssid, StringComparison.Ordinal))
                {
                    verified = true;
                    break;
                }
            }

            if (verified)
            {
                SetCardStatus(card, "VERIFIED: connected to '" + ssid + "' on " + card.Adapter.Name + ".", GreenBrush);
                StatusText.Text = "Connection verified on " + card.Adapter.Name + ".";
            }
            else
            {
                SetCardStatus(card, "Windows accepted the request, but the selected SSID was not verified within 12 seconds. Check signal, password and driver, then refresh.", AmberBrush);
                StatusText.Text = "Connection request accepted; connection state not yet verified.";
            }
            UpdateSummaryCounts();
        }
        catch (Exception ex)
        {
            SetCardStatus(card, "Connection failed: " + ex.Message, RedBrush);
            StatusText.Text = "Connection failed on " + adapter.Name + ".";
            MessageBox.Show(ex.Message, "InfraLap MultiWiFi — " + adapter.Name,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            card.IsBusy = false;
            card.ConnectButton.IsEnabled = true;
            card.DisconnectButton.IsEnabled = true;
        }
    }

    private static string BuildProfileName(WifiInterface adapter)
    {
        var source = string.IsNullOrWhiteSpace(adapter.InterfaceGuid) ? adapter.IdentityKey : adapter.InterfaceGuid;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
        return "InfraLap-MultiWiFi-" + digest;
    }

    private static string AddProfileAndConnect(string profileName, string adapterName, string ssid, string password)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "InfraLap-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            BuildWpa2Profile(profileName, ssid, password).Save(tempPath);
            var addResult = RunNetsh("wlan", "add", "profile",
                "filename=" + tempPath, "interface=" + adapterName, "user=current");
            EnsureNetshSuccess(addResult, "Add Wi-Fi profile");
            var connectResult = RunNetsh("wlan", "connect", "name=" + profileName, "interface=" + adapterName);
            EnsureNetshSuccess(connectResult, "Request Wi-Fi connection");
            return "Windows accepted the connection request.";
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* Best-effort removal of temporary credential XML. */ }
        }
    }

    private static XDocument BuildWpa2Profile(string profileName, string ssid, string password)
    {
        XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        var ssidBytes = Encoding.UTF8.GetBytes(ssid);
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(ns + "WLANProfile",
                new XElement(ns + "name", profileName),
                new XElement(ns + "SSIDConfig",
                    new XElement(ns + "SSID",
                        new XElement(ns + "hex", Convert.ToHexString(ssidBytes)),
                        new XElement(ns + "name", ssid))),
                new XElement(ns + "connectionType", "ESS"),
                new XElement(ns + "connectionMode", "manual"),
                new XElement(ns + "MSM",
                    new XElement(ns + "security",
                        new XElement(ns + "authEncryption",
                            new XElement(ns + "authentication", "WPA2PSK"),
                            new XElement(ns + "encryption", "AES"),
                            new XElement(ns + "useOneX", "false")),
                        new XElement(ns + "sharedKey",
                            new XElement(ns + "keyType", "passPhrase"),
                            new XElement(ns + "protected", "false"),
                            new XElement(ns + "keyMaterial", password))))));
    }

    private async Task DisconnectAdapterAsync(AdapterCard card)
    {
        if (card.IsBusy) return;
        var answer = MessageBox.Show(
            "Disconnect '" + card.Adapter.Name + "' from its current Wi-Fi network? Other adapters will remain connected.",
            "Disconnect this adapter", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        card.IsBusy = true;
        card.ConnectButton.IsEnabled = false;
        card.DisconnectButton.IsEnabled = false;
        SetCardStatus(card, "Sending disconnect request to Windows...", BlueBrush);
        try
        {
            var result = await Task.Run(() => RunNetsh("wlan", "disconnect", "interface=" + card.Adapter.Name));
            EnsureNetshSuccess(result, "Disconnect Wi-Fi adapter");
            await Task.Delay(1000);
            var latest = await Task.Run(ReadNetshInterfaces);
            var current = latest.FirstOrDefault(x => x.Name.Equals(card.Adapter.Name, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                card.Adapter = card.Adapter with { State = current.State, Ssid = current.Ssid, SignalPercent = current.SignalPercent };
                ReplaceInterfaceSnapshot(card.Adapter);
                UpdateCardReadback(card);
            }
            SetCardStatus(card, "Windows accepted the disconnect request for " + card.Adapter.Name + ".", AmberBrush);
            StatusText.Text = "Disconnect request sent for " + card.Adapter.Name + ".";
        }
        catch (Exception ex)
        {
            SetCardStatus(card, "Disconnect failed: " + ex.Message, RedBrush);
            MessageBox.Show(ex.Message, "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            card.IsBusy = false;
            card.ConnectButton.IsEnabled = true;
            card.DisconnectButton.IsEnabled = true;
            UpdateSummaryCounts();
        }
    }

    private void ReplaceInterfaceSnapshot(WifiInterface updated)
    {
        var index = _interfaces.FindIndex(x =>
            string.Equals(x.IdentityKey, updated.IdentityKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.Name, updated.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _interfaces[index] = updated;
    }

    private static List<WifiInterface> ReadNetshInterfaces()
    {
        var result = RunNetsh("wlan", "show", "interfaces");
        EnsureNetshSuccess(result, "Refresh WLAN connection status");
        return ParseInterfaces(result.StandardOutput);
    }

    private async Task RefreshSavedProfilesAsync(bool showStatus = true)
    {
        if (showStatus) StatusText.Text = "Reading saved WLAN profiles from Windows...";
        try
        {
            var selectedKey = _selectedSavedProfile?.AdapterGuid.ToString() + "|" + _selectedSavedProfile?.ProfileName;
            _savedProfiles = await Task.Run(NativeWifiService.GetSavedProfiles);
            SavedProfilesGrid.ItemsSource = _savedProfiles;
            var selected = _savedProfiles.FirstOrDefault(p =>
                string.Equals(p.AdapterGuid.ToString() + "|" + p.ProfileName, selectedKey, StringComparison.OrdinalIgnoreCase));
            SavedProfilesGrid.SelectedItem = selected;
            if (selected is null) ClearSavedProfileDetails();
            SavedProfilesCountText.Text = _savedProfiles.Count.ToString(CultureInfo.InvariantCulture);
            if (showStatus) StatusText.Text = "Loaded " + _savedProfiles.Count + " saved Wi-Fi profile(s).";
        }
        catch (Exception ex)
        {
            _savedProfiles = new List<SavedWifiProfile>();
            SavedProfilesGrid.ItemsSource = _savedProfiles;
            ClearSavedProfileDetails();
            SavedPasswordStatus.Text = "Could not read saved profiles: " + ex.Message;
            if (showStatus) StatusText.Text = "Saved-profile refresh failed: " + ex.Message;
            UpdateSummaryCounts();
        }
    }

    private void SelectSavedProfile()
    {
        _selectedSavedProfile = SavedProfilesGrid.SelectedItem as SavedWifiProfile;
        SavedPasswordHidden.Clear();
        SavedPasswordVisible.Clear();
        SavedPasswordVisible.Visibility = Visibility.Collapsed;
        SavedPasswordHidden.Visibility = Visibility.Visible;
        SavedPasswordToggle.Content = "Show password";
        SavedPasswordToggle.IsEnabled = _selectedSavedProfile is not null;
        if (_selectedSavedProfile is null)
        {
            SavedProfileDetails.Text = "Select a saved network to view its details.";
            SavedPasswordStatus.Text = "Passwords stay hidden until you choose Show password.";
            return;
        }

        var profile = _selectedSavedProfile;
        SavedProfileDetails.Text = profile.Ssid + " | Windows profile: " + profile.ProfileName
            + Environment.NewLine + "Saved on adapter: " + profile.AdapterName + " | Security: " + profile.Authentication;
        var matching = _interfaces.FirstOrDefault(x =>
            Guid.TryParse(NormalizeGuid(x.InterfaceGuid), out var id) && id == profile.AdapterGuid);
        if (matching is not null) SavedTargetAdapter.SelectedItem = matching;

        if (profile.Password is not null)
        {
            SavedPasswordHidden.Password = profile.Password;
            SavedPasswordVisible.Text = profile.Password;
            SavedPasswordStatus.Text = "Password read from the Windows WLAN profile. It is hidden by default.";
        }
        else if (profile.PasswordIsProtected)
            SavedPasswordStatus.Text = "Windows returned an encrypted key. Run InfraLap as Administrator to request plain-text access.";
        else
            SavedPasswordStatus.Text = string.IsNullOrWhiteSpace(profile.PasswordStatus)
                ? "No readable WPA2 passphrase is available for this profile." : profile.PasswordStatus;
    }

    private void ToggleSavedPassword()
    {
        if (_selectedSavedProfile is null) return;
        if (_selectedSavedProfile.Password is null)
        {
            if (_selectedSavedProfile.PasswordIsProtected) OfferAdministratorRestart();
            else MessageBox.Show(SavedPasswordStatus.Text, "Saved Wi-Fi password", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SavedPasswordVisible.Visibility == Visibility.Visible)
        {
            SavedPasswordVisible.Visibility = Visibility.Collapsed;
            SavedPasswordHidden.Visibility = Visibility.Visible;
            SavedPasswordToggle.Content = "Show password";
        }
        else
        {
            SavedPasswordVisible.Text = _selectedSavedProfile.Password;
            SavedPasswordVisible.Visibility = Visibility.Visible;
            SavedPasswordHidden.Visibility = Visibility.Collapsed;
            SavedPasswordToggle.Content = "Hide password";
        }
    }

    private void UseSavedProfileOnTargetAdapter()
    {
        if (_selectedSavedProfile is null)
        {
            MessageBox.Show("Select a saved Windows Wi-Fi profile first.", "InfraLap MultiWiFi",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var profile = _selectedSavedProfile;
        if (!profile.Authentication.Equals("WPA2PSK", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("This connection editor currently creates WPA2-Personal profiles. The selected profile uses '"
                + profile.Authentication + "'. It cannot be converted by this WPA2-only workflow.",
                "Unsupported saved profile type", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (profile.Password is null)
        {
            if (profile.PasswordIsProtected) OfferAdministratorRestart();
            else MessageBox.Show("The selected Windows profile does not contain a readable WPA2 passphrase.",
                "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var target = SavedTargetAdapter.SelectedItem as WifiInterface;
        if (target is null)
        {
            MessageBox.Show("No target Wi-Fi adapter is selected. Refresh the adapters first.",
                "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var card = _adapterCards.FirstOrDefault(x =>
            string.Equals(x.Adapter.IdentityKey, target.IdentityKey, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            MessageBox.Show("The selected adapter card is no longer available. Refresh adapters and try again.",
                "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetCardSsid(card, profile.Ssid);
        card.PasswordInput.Password = profile.Password;
        card.PlainPasswordInput.Text = profile.Password;
        card.PasswordInput.Visibility = Visibility.Visible;
        card.PlainPasswordInput.Visibility = Visibility.Collapsed;
        card.TogglePasswordButton.Content = "Show password";
        SetCardStatus(card, "Saved profile '" + profile.Ssid + "' loaded. Press Connect on this adapter card.", BlueBrush);
        StatusText.Text = "Loaded saved profile '" + profile.Ssid + "' into " + target.Name + ".";
    }

    private void ClearSavedProfileDetails()
    {
        _selectedSavedProfile = null;
        SavedProfileDetails.Text = "Select a saved network to view its details.";
        SavedPasswordHidden.Clear();
        SavedPasswordVisible.Clear();
        SavedPasswordVisible.Visibility = Visibility.Collapsed;
        SavedPasswordHidden.Visibility = Visibility.Visible;
        SavedPasswordToggle.Content = "Show password";
        SavedPasswordToggle.IsEnabled = false;
        SavedPasswordStatus.Text = "Passwords stay hidden until you choose Show password.";
    }

    private void OfferAdministratorRestart()
    {
        var answer = MessageBox.Show(
            "Windows is returning this saved Wi-Fi key in encrypted form. Plain-text key retrieval is normally restricted to administrators. Restart InfraLap as Administrator and select this profile again?",
            "Administrator permission required", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException("Could not determine the application executable path.");
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception ex) { SavedPasswordStatus.Text = "Administrator restart was cancelled or blocked: " + ex.Message; }
    }

    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            StatusText.Text = "Collecting adapter, driver and WLAN diagnostics...";
            var report = await Task.Run(() =>
            {
                var interfaces = RunNetsh("wlan", "show", "interfaces");
                var drivers = RunNetsh("wlan", "show", "drivers");
                var networks = RunNetsh("wlan", "show", "networks", "mode=bssid");
                var hardware = ReadHardwareIdentities();
                var builder = new StringBuilder()
                    .AppendLine("InfraLap MultiWiFi — Multi-adapter diagnostics 1.1")
                    .AppendLine("Created: " + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
                    .AppendLine("OS: " + Environment.OSVersion)
                    .AppendLine("64-bit OS: " + Environment.Is64BitOperatingSystem)
                    .AppendLine("Passwords and saved Wi-Fi keys are not included.")
                    .AppendLine().AppendLine("=== WLAN interfaces ===").AppendLine(interfaces.CombinedOutput)
                    .AppendLine().AppendLine("=== Plug-and-Play hardware identities ===");
                foreach (var item in hardware)
                    builder.AppendLine(item.InterfaceName + " | " + item.AdapterName + " | " + item.Manufacturer
                        + " | " + item.MacAddress + " | " + item.InterfaceGuid + " | " + item.PnpDeviceId);
                builder.AppendLine().AppendLine("=== Driver capabilities ===").AppendLine(drivers.CombinedOutput)
                    .AppendLine().AppendLine("=== Dual-STA driver report (informational) ===")
                    .AppendLine(NativeWifiService.GetDualStaSummary())
                    .AppendLine().AppendLine("=== Nearby networks ===").AppendLine(networks.CombinedOutput);
                return builder.ToString();
            });

            var dialog = new SaveFileDialog
            {
                Title = "Save InfraLap MultiWiFi diagnostics",
                FileName = "InfraLap-MultiWiFi-Diagnostics.txt",
                Filter = "Text files (*.txt)|*.txt"
            };
            if (dialog.ShowDialog() == true)
            {
                await File.WriteAllTextAsync(dialog.FileName, report, new UTF8Encoding(false));
                StatusText.Text = "Diagnostics exported. Wi-Fi passwords are not included.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Diagnostics export failed: " + ex.Message;
            MessageBox.Show(ex.Message, "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static List<WifiInterface> ParseInterfaces(string output)
    {
        var results = new List<WifiInterface>();
        string? name = null;
        var state = "Unknown";
        var description = "";
        var ssid = "";
        var guid = "";
        var mac = "";
        var signal = -1;
        var channel = -1;
        var radioType = "";
        var bssid = "";
        var receiveRate = "";
        var transmitRate = "";

        void AddCurrent()
        {
            if (!string.IsNullOrWhiteSpace(name))
                results.Add(new WifiInterface(name, state, description, ssid, guid, mac, "", "",
                    signal, channel, radioType, bssid, receiveRate, transmitRate));
        }

        foreach (var raw in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                AddCurrent();
                name = value; state = "Unknown"; description = ""; ssid = ""; guid = ""; mac = "";
                signal = -1; channel = -1; radioType = ""; bssid = ""; receiveRate = ""; transmitRate = "";
            }
            else if (key.Equals("State", StringComparison.OrdinalIgnoreCase)) state = string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
            else if (key.Equals("Description", StringComparison.OrdinalIgnoreCase)) description = value;
            else if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase)) ssid = value;
            else if (key.Equals("GUID", StringComparison.OrdinalIgnoreCase)) guid = value;
            else if (key.Equals("Physical address", StringComparison.OrdinalIgnoreCase)) mac = value;
            else if (key.Equals("Signal", StringComparison.OrdinalIgnoreCase))
            {
                var numeric = value.Trim().TrimEnd('%');
                if (int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) signal = parsed;
            }
            else if (key.Equals("Channel", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) channel = parsed;
            }
            else if (key.Equals("Radio type", StringComparison.OrdinalIgnoreCase)) radioType = value;
            else if (key.Equals("BSSID", StringComparison.OrdinalIgnoreCase)) bssid = value;
            else if (key.Equals("Receive rate (Mbps)", StringComparison.OrdinalIgnoreCase)) receiveRate = value;
            else if (key.Equals("Transmit rate (Mbps)", StringComparison.OrdinalIgnoreCase)) transmitRate = value;
        }
        AddCurrent();
        return results;
    }

    private static NetshResult RunNetsh(params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe", UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        return new NetshResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static void EnsureNetshSuccess(NetshResult result, string operation)
    {
        var output = result.CombinedOutput;
        var hasErrorText = output.Contains("There is no wireless interface", StringComparison.OrdinalIgnoreCase)
            || output.Contains("The profile is not found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("The parameter is incorrect", StringComparison.OrdinalIgnoreCase)
            || output.Contains("The system cannot find", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Error:", StringComparison.OrdinalIgnoreCase);
        if (result.ExitCode != 0 || hasErrorText)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? operation + " failed. netsh exit code: " + result.ExitCode + "." : output.Trim());
    }
}
