using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Principal;
using System.ComponentModel;
using System.Windows;
using System.Xml.Linq;
using Microsoft.Win32;

namespace InfraLap.MultiWiFi;

public partial class MainWindow : Window
{
    private sealed record WifiInterface(string Name, string State, string Description, string Ssid)
    {
        public string DisplayText => string.IsNullOrWhiteSpace(Description)
            ? $"{Name} — {State}"
            : $"{Name} — {Description} — {State}";
    }

    private sealed record NetshResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => string.Join(Environment.NewLine,
            new[] { StandardOutput, StandardError }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private List<WifiInterface> _interfaces = new();
    private List<SavedWifiProfile> _savedProfiles = new();
    private SavedWifiProfile? _selectedSavedProfile;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAllAsync();
        Closed += (_, _) => { Connection1Password.Clear(); Connection2Password.Clear(); SavedPasswordHidden.Clear(); SavedPasswordVisible.Clear(); };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanNetworksAsync();
    private async void SavedProfiles_Click(object sender, RoutedEventArgs e) => await RefreshSavedProfilesAsync();
    private void SavedProfilesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SelectSavedProfile();
    private void ToggleSavedPassword_Click(object sender, RoutedEventArgs e) => ToggleSavedPassword();
    private void UseSavedForConnection1_Click(object sender, RoutedEventArgs e) => UseSavedProfile(1);
    private void UseSavedForConnection2_Click(object sender, RoutedEventArgs e) => UseSavedProfile(2);
    private async void Connect1_Click(object sender, RoutedEventArgs e) => await ConnectProfileAsync(1);
    private async void Connect2_Click(object sender, RoutedEventArgs e) => await ConnectProfileAsync(2);
    private async void Disconnect1_Click(object sender, RoutedEventArgs e) => await DisconnectProfileAsync(1);
    private async void Disconnect2_Click(object sender, RoutedEventArgs e) => await DisconnectProfileAsync(2);

    private async Task RefreshAllAsync()
    {
        var old1 = (Connection1Adapter.SelectedItem as WifiInterface)?.Name;
        var old2 = (Connection2Adapter.SelectedItem as WifiInterface)?.Name;
        StatusText.Text = "Refreshing Windows wireless interfaces...";
        try
        {
            var output = await Task.Run(() => RunNetsh("wlan", "show", "interfaces"));
            EnsureNetshSuccess(output, "Read wireless interfaces");
            _interfaces = ParseInterfaces(output.StandardOutput);

            Connection1Adapter.ItemsSource = _interfaces;
            Connection2Adapter.ItemsSource = _interfaces;
            Connection1Adapter.SelectedItem = _interfaces.FirstOrDefault(x => x.Name == old1)
                ?? _interfaces.ElementAtOrDefault(0);
            Connection2Adapter.SelectedItem = _interfaces.FirstOrDefault(x => x.Name == old2)
                ?? _interfaces.ElementAtOrDefault(_interfaces.Count > 1 ? 1 : 0);

            UpdateProfileStatus(1);
            UpdateProfileStatus(2);

            var dualSta = await Task.Run(() => NativeWifiService.GetDualStaSummary());
            var adapterSummary = _interfaces.Count == 0
                ? "No wireless interfaces were reported by Windows."
                : _interfaces.Count.ToString() + " Windows wireless interface(s) detected.";
            HardwareStatus.Text = adapterSummary + Environment.NewLine + dualSta;

            await ScanNetworksAsync(showStatus: false);
            await RefreshSavedProfilesAsync(showStatus: false);
            StatusText.Text = "Refresh complete: " + _interfaces.Count + " wireless interface(s) detected; saved profiles loaded.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Refresh failed: " + ex.Message;
            HardwareStatus.Text = "Could not read Wi-Fi interfaces. Check the Windows WLAN service and adapter driver.";
            MessageBox.Show(ex.Message, "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ScanNetworksAsync(bool showStatus = true)
    {
        try
        {
            if (showStatus) StatusText.Text = "Scanning nearby Wi-Fi networks...";
            var result = await Task.Run(() => RunNetsh("wlan", "show", "networks", "mode=bssid"));
            EnsureNetshSuccess(result, "Scan nearby networks");
            NetworksOutput.Text = result.StandardOutput;
            if (showStatus) StatusText.Text = "Nearby Wi-Fi scan completed.";
        }
        catch (Exception ex)
        {
            NetworksOutput.Text = ex.Message;
            if (showStatus) StatusText.Text = "Wi-Fi scan failed: " + ex.Message;
        }
    }

    private async Task RefreshSavedProfilesAsync(bool showStatus = true)
    {
        if (showStatus) StatusText.Text = "Reading saved Wi-Fi profiles from Windows...";
        try
        {
            var selectedKey = _selectedSavedProfile?.AdapterGuid.ToString() + "|" + _selectedSavedProfile?.ProfileName;
            _savedProfiles = await Task.Run(NativeWifiService.GetSavedProfiles);
            SavedProfilesGrid.ItemsSource = _savedProfiles;
            var selected = _savedProfiles.FirstOrDefault(p =>
                string.Equals(p.AdapterGuid.ToString() + "|" + p.ProfileName, selectedKey, StringComparison.OrdinalIgnoreCase));
            SavedProfilesGrid.SelectedItem = selected;
            if (selected is null) ClearSavedProfileDetails();
            if (showStatus) StatusText.Text = "Loaded " + _savedProfiles.Count + " saved Wi-Fi profile(s).";
        }
        catch (Exception ex)
        {
            _savedProfiles = new List<SavedWifiProfile>();
            SavedProfilesGrid.ItemsSource = _savedProfiles;
            ClearSavedProfileDetails();
            SavedPasswordStatus.Text = "Could not read saved profiles: " + ex.Message;
            if (showStatus) StatusText.Text = "Saved-profile refresh failed: " + ex.Message;
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
            SavedPasswordStatus.Text = "Passwords remain hidden until you choose Show password.";
            return;
        }

        var profile = _selectedSavedProfile;
        SavedProfileDetails.Text = profile.Ssid + "  |  Profile: " + profile.ProfileName + Environment.NewLine
            + "Adapter: " + profile.AdapterName + "  |  Security: " + profile.Authentication;
        if (profile.Password is not null)
        {
            SavedPasswordHidden.Password = profile.Password;
            SavedPasswordVisible.Text = profile.Password;
            SavedPasswordStatus.Text = "Password retrieved from the Windows profile. It is hidden by default.";
        }
        else if (profile.PasswordIsProtected)
        {
            SavedPasswordStatus.Text = "Windows returned an encrypted password. Run InfraLap as Administrator to request plain-text access.";
        }
        else
        {
            SavedPasswordStatus.Text = string.IsNullOrWhiteSpace(profile.PasswordStatus)
                ? "No saved WPA2 password is available for this profile."
                : profile.PasswordStatus;
        }
    }

    private void ToggleSavedPassword()
    {
        if (_selectedSavedProfile is null) return;
        if (_selectedSavedProfile.Password is null)
        {
            if (_selectedSavedProfile.PasswordIsProtected)
                OfferAdministratorRestart();
            else
                MessageBox.Show(SavedPasswordStatus.Text, "Saved Wi-Fi password", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void UseSavedProfile(int slot)
    {
        if (_selectedSavedProfile is null)
        {
            MessageBox.Show("Select a saved Wi-Fi profile first.", "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var profile = _selectedSavedProfile;
        if (!profile.Authentication.Equals("WPA2PSK", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("This UAT connection editor currently creates WPA2-Personal profiles. The selected Windows profile uses '"
                + profile.Authentication + "'. It is shown here, but cannot safely be converted by this WPA2-only connection workflow.",
                "Unsupported saved profile type", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (profile.Password is null)
        {
            if (profile.PasswordIsProtected) OfferAdministratorRestart();
            else MessageBox.Show("The saved profile does not contain a readable WPA2 passphrase.", "InfraLap MultiWiFi",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var adapter = _interfaces.FirstOrDefault(x => x.Name.Equals(profile.AdapterName, StringComparison.OrdinalIgnoreCase));
        if (slot == 1)
        {
            Connection1Ssid.Text = profile.Ssid;
            Connection1Password.Password = profile.Password;
            if (adapter is not null) Connection1Adapter.SelectedItem = adapter;
        }
        else
        {
            Connection2Ssid.Text = profile.Ssid;
            Connection2Password.Password = profile.Password;
            if (adapter is not null) Connection2Adapter.SelectedItem = adapter;
        }
        StatusText.Text = "Loaded saved profile '" + profile.Ssid + "' into Connection " + slot + ". Press Connect to use it.";
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
        SavedPasswordStatus.Text = "Passwords remain hidden until you choose Show password.";
    }

    private void OfferAdministratorRestart()
    {
        var answer = MessageBox.Show(
            "Windows is returning this saved Wi-Fi key in encrypted form. Plain-text key retrieval is restricted by Windows and is normally available only to administrators. Restart InfraLap as Administrator and then select this profile again?",
            "Administrator permission required",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException("The application executable path could not be determined.");
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception ex)
        {
            SavedPasswordStatus.Text = "Administrator restart was cancelled or blocked: " + ex.Message;
        }
        catch (Exception ex)
        {
            SavedPasswordStatus.Text = "Could not restart as Administrator: " + ex.Message;
        }
    }

    private async Task ConnectProfileAsync(int slot)
    {
        var adapter = slot == 1
            ? Connection1Adapter.SelectedItem as WifiInterface
            : Connection2Adapter.SelectedItem as WifiInterface;
        var ssid = (slot == 1 ? Connection1Ssid.Text : Connection2Ssid.Text).Trim();
        var password = slot == 1 ? Connection1Password.Password : Connection2Password.Password;
        var otherAdapter = slot == 1
            ? Connection2Adapter.SelectedItem as WifiInterface
            : Connection1Adapter.SelectedItem as WifiInterface;
        var otherSsid = (slot == 1 ? Connection2Ssid.Text : Connection1Ssid.Text).Trim();

        if (adapter is null)
        {
            SetProfileStatus(slot, "No adapter selected. Refresh Wi-Fi adapters first.", "#F0B36A");
            return;
        }
        if (string.IsNullOrWhiteSpace(ssid))
        {
            SetProfileStatus(slot, "Enter the Wi-Fi network name (SSID).", "#F0B36A");
            return;
        }
        if (Encoding.UTF8.GetByteCount(ssid) > 32)
        {
            SetProfileStatus(slot, "The SSID is too long. Wi-Fi network names are limited to 32 UTF-8 bytes.", "#FF9292");
            return;
        }
        if (password.Length < 8 || password.Length > 63)
        {
            SetProfileStatus(slot, "For WPA2-Personal, enter a password from 8 to 63 characters.", "#F0B36A");
            return;
        }

        SetProfileStatus(slot, "Creating a temporary Windows Wi-Fi profile...", "#BDD2FF");
        StatusText.Text = $"Preparing Connection {slot} for adapter '{adapter.Name}'...";

        try
        {
            var sameAdapterAsOtherProfile = otherAdapter is not null
                && string.Equals(adapter.Name, otherAdapter.Name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(otherSsid);

            var profileName = slot == 1 ? "InfraLap-MultiWiFi-Connection-1" : "InfraLap-MultiWiFi-Connection-2";
            var message = await Task.Run(() => AddProfileAndConnect(
                profileName, adapter.Name, ssid, password, sameAdapterAsOtherProfile));

            if (slot == 1) Connection1Password.Clear();
            else Connection2Password.Clear();

            if (message.StartsWith("PROFILE SAVED:", StringComparison.Ordinal))
            {
                SetProfileStatus(slot, message["PROFILE SAVED:".Length..].Trim(), "#F0B36A");
                StatusText.Text = $"Connection {slot} profile saved in Windows; a second independent adapter is required for simultaneous use.";
            }
            else
            {
                SetProfileStatus(slot, message, "#83DDB5");
                StatusText.Text = $"Connection {slot}: Windows accepted the connection request. Refresh adapters to confirm the active SSID.";
                await Task.Delay(1800);
                await RefreshAllAsync();
            }
        }
        catch (Exception ex)
        {
            SetProfileStatus(slot, "Connection failed: " + ex.Message, "#FF9292");
            StatusText.Text = $"Connection {slot} failed.";
            MessageBox.Show(ex.Message, $"InfraLap MultiWiFi — Connection {slot}",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string AddProfileAndConnect(
        string profileName, string adapterName, string ssid, string password, bool sameAdapterAsOtherProfile)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"InfraLap-{Guid.NewGuid():N}.xml");
        try
        {
            var document = BuildWpa2Profile(profileName, ssid, password);
            document.Save(tempPath);

            var addResult = RunNetsh("wlan", "add", "profile",
                $"filename={tempPath}", $"interface={adapterName}", "user=current");
            EnsureNetshSuccess(addResult, "Add Wi-Fi profile");

            if (sameAdapterAsOtherProfile)
                return "PROFILE SAVED: The credentials were added to Windows. This adapter is also assigned to the other profile, so this UAT build has not switched its active network. To keep both networks connected, assign a second physical Wi-Fi adapter to this profile.";

            var connectResult = RunNetsh("wlan", "connect",
                $"name={profileName}", $"interface={adapterName}");
            EnsureNetshSuccess(connectResult, "Request Wi-Fi connection");

            return $"Windows accepted the connection request for '{ssid}' on '{adapterName}'. Refresh adapters to verify the actual connected SSID.";
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

    private async Task DisconnectProfileAsync(int slot)
    {
        var adapter = slot == 1
            ? Connection1Adapter.SelectedItem as WifiInterface
            : Connection2Adapter.SelectedItem as WifiInterface;

        if (adapter is null)
        {
            SetProfileStatus(slot, "Select a Wi-Fi adapter first.", "#F0B36A");
            return;
        }

        var answer = MessageBox.Show(
            $"Disconnect adapter '{adapter.Name}'? This may interrupt any other network using that adapter.",
            $"Disconnect Connection {slot}", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            SetProfileStatus(slot, "Disconnecting adapter...", "#BDD2FF");
            var result = await Task.Run(() => RunNetsh("wlan", "disconnect", $"interface={adapter.Name}"));
            EnsureNetshSuccess(result, "Disconnect Wi-Fi adapter");
            SetProfileStatus(slot, $"Windows accepted the disconnect request for '{adapter.Name}'.", "#F0B36A");
            StatusText.Text = $"Connection {slot}: disconnect request accepted.";
        }
        catch (Exception ex)
        {
            SetProfileStatus(slot, "Disconnect failed: " + ex.Message, "#FF9292");
            MessageBox.Show(ex.Message, $"Disconnect Connection {slot}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Collecting local wireless diagnostics...";
            var report = await Task.Run(() =>
            {
                var interfaces = RunNetsh("wlan", "show", "interfaces");
                var drivers = RunNetsh("wlan", "show", "drivers");
                var networks = RunNetsh("wlan", "show", "networks", "mode=bssid");

                return new StringBuilder()
                    .AppendLine("InfraLap MultiWiFi — Two-profile UAT diagnostics")
                    .AppendLine($"Created: {DateTimeOffset.Now:O}")
                    .AppendLine($"OS: {Environment.OSVersion}")
                    .AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}")
                    .AppendLine("Passwords are not included in this report.")
                    .AppendLine("=== Dual-STA readiness ===")
                    .AppendLine(NativeWifiService.GetDualStaSummary())
                    .AppendLine()
                    .AppendLine("=== WLAN interfaces ===")
                    .AppendLine(interfaces.CombinedOutput)
                    .AppendLine()
                    .AppendLine("=== WLAN drivers ===")
                    .AppendLine(drivers.CombinedOutput)
                    .AppendLine()
                    .AppendLine("=== Nearby Wi-Fi networks ===")
                    .AppendLine(networks.CombinedOutput)
                    .ToString();
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
                StatusText.Text = "Diagnostics exported. Passwords are not included.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Diagnostics export failed: " + ex.Message;
            MessageBox.Show(ex.Message, "InfraLap MultiWiFi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateProfileStatus(int slot)
    {
        var adapter = slot == 1
            ? Connection1Adapter.SelectedItem as WifiInterface
            : Connection2Adapter.SelectedItem as WifiInterface;
        var targetSsid = (slot == 1 ? Connection1Ssid.Text : Connection2Ssid.Text).Trim();

        if (adapter is null)
        {
            SetProfileStatus(slot, "No wireless adapter available.", "#F0B36A");
            return;
        }

        if (string.Equals(adapter.State, "connected", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(targetSsid)
                && !string.IsNullOrWhiteSpace(adapter.Ssid)
                && !string.Equals(targetSsid, adapter.Ssid, StringComparison.Ordinal))
                SetProfileStatus(slot, $"Adapter is connected to '{adapter.Ssid}', not this profile's target '{targetSsid}'.", "#F0B36A");
            else
                SetProfileStatus(slot, $"Adapter connected to '{(string.IsNullOrWhiteSpace(adapter.Ssid) ? "an SSID not reported by Windows" : adapter.Ssid)}'.", "#83DDB5");
        }
        else
        {
            SetProfileStatus(slot, $"Adapter '{adapter.Name}' is {adapter.State}.", "#F0B36A");
        }
    }

    private void SetProfileStatus(int slot, string message, string colorHex)
    {
        var target = slot == 1 ? Connection1Status : Connection2Status;
        target.Text = message;
        target.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(colorHex)!;
    }

    private static List<WifiInterface> ParseInterfaces(string output)
    {
        var results = new List<WifiInterface>();
        string? name = null;
        var state = "Unknown";
        var description = "";
        var ssid = "";

        void AddCurrent()
        {
            if (!string.IsNullOrWhiteSpace(name))
                results.Add(new WifiInterface(name, state, description, ssid));
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
                name = value;
                state = "Unknown";
                description = "";
                ssid = "";
            }
            else if (key.Equals("State", StringComparison.OrdinalIgnoreCase))
                state = string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
            else if (key.Equals("Description", StringComparison.OrdinalIgnoreCase))
                description = value;
            else if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase))
                ssid = value;
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
                FileName = "netsh.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

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
                ? $"{operation} failed. netsh exit code: {result.ExitCode}."
                : output.Trim());
    }
}
