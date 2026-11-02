using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace InfraLap.MultiWiFi;

public partial class MainWindow : Window
{
    private sealed record WifiInterface(string Name, string State, string Description);

    private List<WifiInterface> _interfaces = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        StatusText.Text = "Scanning Windows Wi-Fi interfaces and nearby networks...";
        try
        {
            var result = await Task.Run(() =>
            {
                var interfaces = RunNetsh("wlan", "show", "interfaces");
                var networks = RunNetsh("wlan", "show", "networks", "mode=bssid");
                return (interfaces, networks, ParseInterfaces(interfaces));
            });

            _interfaces = result.Item3;
            InterfacesList.ItemsSource = _interfaces.Select(x => $"{x.Name}  |  {x.State}").ToList();
            if (InterfacesList.Items.Count > 0 && InterfacesList.SelectedIndex < 0)
                InterfacesList.SelectedIndex = 0;
            NetworksOutput.Text = result.networks;
            StatusText.Text = $"Scan complete. Windows reports {_interfaces.Count} wireless interface(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed: " + ex.Message;
            MessageBox.Show(ex.Message, "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InterfacesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = InterfacesList.SelectedIndex;
        if (index >= 0 && index < _interfaces.Count)
            InterfaceNameBox.Text = _interfaces[index].Name;
    }

    private async void Drivers_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Collecting wireless driver information...";
        try
        {
            var output = await Task.Run(() => RunNetsh("wlan", "show", "drivers"));
            NetworksOutput.Text = output;
            StatusText.Text = "Driver information loaded. Export diagnostics to save a copy.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var profile = ProfileNameBox.Text.Trim();
        var iface = InterfaceNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(iface))
        {
            MessageBox.Show("Select a Wi-Fi interface and enter the exact name of a saved Windows Wi-Fi profile.");
            return;
        }

        var answer = MessageBox.Show(
            $"Request Windows to connect interface '{iface}' using saved profile '{profile}'?\n\nThis may interrupt your current connection.",
            "Connect saved Wi-Fi profile", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            StatusText.Text = await Task.Run(() => RunNetsh("wlan", "connect", $"name={profile}", $"interface={iface}"));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var iface = InterfaceNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(iface))
        {
            MessageBox.Show("Select a Wi-Fi interface first.");
            return;
        }
        var answer = MessageBox.Show($"Disconnect interface '{iface}'?", "Disconnect Wi-Fi",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            StatusText.Text = await Task.Run(() => RunNetsh("wlan", "disconnect", $"interface={iface}"));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Collecting diagnostics...";
            var report = await Task.Run(() => new StringBuilder()
                .AppendLine("InfraLap MultiWiFi — Phase A UAT diagnostics")
                .AppendLine($"Created: {DateTimeOffset.Now:O}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}")
                .AppendLine()
                .AppendLine("=== WLAN interfaces ===")
                .AppendLine(RunNetsh("wlan", "show", "interfaces"))
                .AppendLine()
                .AppendLine("=== WLAN drivers ===")
                .AppendLine(RunNetsh("wlan", "show", "drivers"))
                .AppendLine()
                .AppendLine("=== Nearby Wi-Fi networks ===")
                .AppendLine(RunNetsh("wlan", "show", "networks", "mode=bssid"))
                .ToString());

            var dialog = new SaveFileDialog
            {
                Title = "Save InfraLap MultiWiFi diagnostics",
                FileName = "InfraLap-MultiWiFi-Diagnostics.txt",
                Filter = "Text files (*.txt)|*.txt"
            };
            if (dialog.ShowDialog() == true)
            {
                await File.WriteAllTextAsync(dialog.FileName, report, Encoding.UTF8);
                StatusText.Text = "Diagnostics exported successfully.";
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static List<WifiInterface> ParseInterfaces(string output)
    {
        var results = new List<WifiInterface>();
        string? name = null, state = "Unknown", description = "";
        foreach (var raw in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.StartsWith("Name", StringComparison.OrdinalIgnoreCase) && TryValue(line, out var v))
            {
                if (!string.IsNullOrEmpty(name)) results.Add(new WifiInterface(name, state, description));
                name = v; state = "Unknown"; description = "";
            }
            else if (line.StartsWith("Description", StringComparison.OrdinalIgnoreCase) && TryValue(line, out v))
                description = v;
            else if (line.StartsWith("State", StringComparison.OrdinalIgnoreCase) && TryValue(line, out v))
                state = v;
        }
        if (!string.IsNullOrEmpty(name)) results.Add(new WifiInterface(name, state, description));
        return results;
    }

    private static bool TryValue(string line, out string value)
    {
        var separator = line.IndexOf(':');
        if (separator >= 0) { value = line[(separator + 1)..].Trim(); return true; }
        value = ""; return false;
    }

    private static string RunNetsh(params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        var output = stdoutTask.Result;
        var error = stderrTask.Result;
        return string.IsNullOrWhiteSpace(error) ? output : output + Environment.NewLine + error;
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = "Operation failed: " + ex.Message;
        MessageBox.Show(ex.Message, "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
