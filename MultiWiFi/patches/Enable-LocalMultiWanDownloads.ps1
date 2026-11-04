$ErrorActionPreference = 'Stop'
$root = $env:V2_PROJECT_ROOT
if ([string]::IsNullOrWhiteSpace($root)) { throw 'V2_PROJECT_ROOT is not set.' }

$app = Join-Path $root 'Client/src/InfraLap.MultiWiFi.App'
$services = Join-Path $app 'Services'
New-Item -ItemType Directory -Force -Path $services | Out-Null

$service = @'
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using InfraLap.MultiWiFi.Models;

namespace InfraLap.MultiWiFi.Services;

public sealed record LocalMultiWanDownloadResult(
    long TotalBytes,
    TimeSpan Elapsed,
    IReadOnlyDictionary<string, long> BytesByAdapter);

/// <summary>
/// Downloads a single HTTP range-capable resource by opening independent TCP
/// connections bound to different local adapter IPv4 addresses. No relay/VPS
/// is used. This is application-level download acceleration, not a system-wide
/// multipath VPN.
/// </summary>
public static class LocalMultiWanDownloadService
{
    public static async Task<LocalMultiWanDownloadResult> DownloadAsync(
        Uri uri,
        string destinationPath,
        IReadOnlyList<PathChoice> selectedPaths,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(selectedPaths);

        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only HTTP and HTTPS download URLs are supported.", nameof(uri));

        var paths = selectedPaths
            .Where(p => p is not null && p.Address.AddressFamily == AddressFamily.InterNetwork)
            .DistinctBy(p => p.Address)
            .ToArray();

        if (paths.Length < 2 || paths.Length > 5)
            throw new ArgumentException("Select between two and five active, distinct IPv4 adapters.", nameof(selectedPaths));

        foreach (var path in paths)
        {
            if (!NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(path.Address))))
                throw new InvalidOperationException($"Adapter '{path.Name}' no longer owns address {path.Address}. Refresh adapters and try again.");
        }

        var fullDestination = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(fullDestination)
            ?? throw new ArgumentException("The destination folder is invalid.", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);

        var stopwatch = Stopwatch.StartNew();
        var clients = paths.ToDictionary(p => p, p => CreateBoundClient(p.Address));
        var workingDirectory = Path.Combine(destinationDirectory, ".infralap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var partFiles = new List<string>();

        try
        {
            var probe = await ProbeRangeSupportAsync(uri, clients[paths[0]], cancellationToken).ConfigureAwait(false);
            var totalLength = probe.TotalLength;
            if (totalLength <= 0)
                throw new InvalidOperationException("The server did not report a valid file size.");

            var segmentCount = (int)Math.Min((long)paths.Length * 2L, totalLength);
            segmentCount = Math.Max(1, segmentCount);
            var pathBytes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long completedBytes = 0;
            var tasks = new List<Task>();

            for (var i = 0; i < segmentCount; i++)
            {
                var start = (long)((decimal)totalLength * i / segmentCount);
                var end = (long)((decimal)totalLength * (i + 1) / segmentCount) - 1;
                var path = paths[i % paths.Length];
                var partPath = Path.Combine(workingDirectory, $"part-{i:D3}.bin");
                partFiles.Add(partPath);
                tasks.Add(DownloadRangeAsync(
                    uri, clients[path], path, start, end, totalLength, probe,
                    partPath, pathBytes, value =>
                    {
                        var current = Interlocked.Add(ref completedBytes, value);
                        progress?.Invoke(current, totalLength);
                    }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var mergedPath = fullDestination + ".infralap-" + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var output = new FileStream(
                    mergedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    foreach (var partPath in partFiles)
                    {
                        await using var input = new FileStream(
                            partPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (output.Length != totalLength)
                        throw new InvalidDataException($"Reassembled file size mismatch. Expected {totalLength} bytes, received {output.Length} bytes.");
                }
                File.Move(mergedPath, fullDestination, overwrite: true);
            }
            finally
            {
                if (File.Exists(mergedPath)) File.Delete(mergedPath);
            }

            stopwatch.Stop();
            return new LocalMultiWanDownloadResult(
                totalLength, stopwatch.Elapsed, pathBytes.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var client in clients.Values) client.Dispose();
            try { if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, recursive: true); } catch { }
        }
    }

    private static HttpClient CreateBoundClient(IPAddress localAddress)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 4,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
                var remote = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? throw new SocketException((int)SocketError.HostNotFound);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(localAddress, 0));
                    await socket.ConnectAsync(new IPEndPoint(remote, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed record RangeProbe(long TotalLength, EntityTagHeaderValue? ETag, DateTimeOffset? LastModified);

    private static async Task<RangeProbe> ProbeRangeSupportAsync(
        Uri uri, HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new NotSupportedException("This download server did not return HTTP 206 Partial Content for a byte-range request. This URL cannot be accelerated by the VPS-free mode.");

        var range = response.Content.Headers.ContentRange;
        if (range is null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            range.From != 0 || range.To != 0 || range.Length is null or <= 0)
            throw new InvalidDataException("The server returned an invalid Content-Range header.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var oneByte = new byte[1];
        var read = await stream.ReadAsync(oneByte.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (read != 1)
            throw new InvalidDataException("The server returned an empty range probe.");

        return new RangeProbe(range.Length.Value, response.Headers.ETag, response.Content.Headers.LastModified);
    }

    private static async Task DownloadRangeAsync(
        Uri uri,
        HttpClient client,
        PathChoice path,
        long start,
        long end,
        long totalLength,
        RangeProbe probe,
        string partPath,
        ConcurrentDictionary<string, long> pathBytes,
        Action<long> onBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (probe.ETag is { IsWeak: false } etag)
            request.Headers.IfRange = new RangeConditionHeaderValue(etag);
        else if (probe.LastModified is DateTimeOffset modified)
            request.Headers.IfRange = new RangeConditionHeaderValue(modified);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new InvalidDataException($"Adapter '{path.Name}' received HTTP {(int)response.StatusCode} instead of 206 for byte range {start}-{end}. The server may not support stable parallel ranges.");

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is null || contentRange.From != start || contentRange.To != end || contentRange.Length != totalLength)
            throw new InvalidDataException($"Adapter '{path.Name}' received an inconsistent Content-Range for bytes {start}-{end}.");

        if (probe.ETag is not null && response.Headers.ETag is not null &&
            !string.Equals(probe.ETag.ToString(), response.Headers.ETag.ToString(), StringComparison.Ordinal))
            throw new InvalidDataException("The server's ETag changed during the download. The partial file was discarded for safety.");

        if (probe.ETag is null && probe.LastModified is not null &&
            response.Content.Headers.LastModified is DateTimeOffset changed && changed != probe.LastModified)
            throw new InvalidDataException("The server's Last-Modified value changed during the download. The partial file was discarded for safety.");

        var expected = end - start + 1;
        long written = 0;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            written += read;
            if (written > expected)
                throw new InvalidDataException($"Adapter '{path.Name}' received more bytes than requested.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            pathBytes.AddOrUpdate(path.Name, read, (_, current) => current + read);
            onBytes(read);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (written != expected)
            throw new EndOfStreamException($"Adapter '{path.Name}' returned {written} of {expected} requested bytes.");
    }
}
'@

$window = @'
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using InfraLap.MultiWiFi.Models;
using InfraLap.MultiWiFi.Services;

namespace InfraLap.MultiWiFi;

public sealed class LocalMultiWanDownloadWindow : Window
{
    private readonly IReadOnlyList<PathChoice> _availablePaths;
    private readonly List<(PathChoice Path, CheckBox CheckBox)> _checks = new();
    private readonly TextBox _url = new();
    private readonly TextBox _destination = new();
    private readonly ProgressBar _progress = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _pathStats = new();
    private Button _start = new();
    private Button _cancel = new();
    private CancellationTokenSource? _cancellation;

    public LocalMultiWanDownloadWindow(IReadOnlyList<PathChoice> paths)
    {
        _availablePaths = paths?.ToArray() ?? Array.Empty<PathChoice>();
        Title = "InfraLap Multi-WAN Download — No VPS";
        Width = 900;
        Height = 720;
        MinWidth = 720;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 17, 31));
        BuildLayout();
        Closed += (_, _) => _cancellation?.Cancel();
    }

    private void BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = "LOCAL MULTI-WAN DOWNLOAD",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        });
        root.Children.Add(new TextBlock
        {
            Text = "No VPS · No relay · Each download segment is connected directly through a different selected adapter.",
            Foreground = new SolidColorBrush(Color.FromRgb(159, 177, 201)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 18)
        });

        root.Children.Add(Label("HTTP / HTTPS file URL"));
        _url.MinHeight = 38;
        _url.FontSize = 14;
        _url.Margin = new Thickness(0, 4, 0, 14);
        _url.Text = "";
        root.Children.Add(_url);

        root.Children.Add(Label("Choose 2–5 independent internet adapters"));
        root.Children.Add(new TextBlock
        {
            Text = "This mode works only when the server supports stable HTTP byte ranges. Connections are made directly from your laptop; it does not combine all browser or application traffic.",
            Foreground = new SolidColorBrush(Color.FromRgb(159, 177, 201)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 9)
        });
        var pathPanel = new WrapPanel();
        foreach (var path in _availablePaths.Take(5))
        {
            var check = new CheckBox
            {
                Content = $"{path.Name}  ·  {path.Address}  ·  {path.Gateway}",
                IsChecked = path.IsSelected,
                Foreground = Brushes.White,
                Margin = new Thickness(8, 7, 20, 7),
                ToolTip = path.Description
            };
            _checks.Add((path, check));
            pathPanel.Children.Add(check);
        }
        root.Children.Add(pathPanel);

        root.Children.Add(Label("Save downloaded file as"));
        var saveRow = new DockPanel { Margin = new Thickness(0, 4, 0, 14) };
        _destination.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "infralap-download.bin");
        _destination.MinHeight = 36;
        _destination.VerticalContentAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_destination, Dock.Left);
        saveRow.Children.Add(_destination);
        var browse = MakeButton("Browse…", Color.FromRgb(55, 75, 101));
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += (_, _) => BrowseDestination();
        DockPanel.SetDock(browse, Dock.Right);
        saveRow.Children.Add(browse);
        root.Children.Add(saveRow);

        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Height = 16;
        _progress.Margin = new Thickness(0, 4, 0, 12);
        root.Children.Add(_progress);

        _status.Text = "Ready. Select two different internet connections and a range-capable file URL.";
        _status.Foreground = Brushes.White;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(_status);

        _pathStats.Text = "";
        _pathStats.Foreground = new SolidColorBrush(Color.FromRgb(112, 214, 166));
        _pathStats.TextWrapping = TextWrapping.Wrap;
        _pathStats.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(_pathStats);

        var buttons = new WrapPanel();
        _start = MakeButton("Start VPS-free download", Color.FromRgb(22, 132, 92));
        _start.Click += async (_, _) => await StartDownloadAsync();
        buttons.Children.Add(_start);
        _cancel = MakeButton("Cancel", Color.FromRgb(143, 63, 80));
        _cancel.IsEnabled = false;
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        buttons.Children.Add(_cancel);
        root.Children.Add(buttons);

        root.Children.Add(new TextBlock
        {
            Text = "Limit: this is direct multi-connection file downloading, not transparent whole-PC bandwidth bonding. Unsupported servers, streaming sites, and ordinary single TCP sessions will not be accelerated by this mode.",
            Foreground = new SolidColorBrush(Color.FromRgb(245, 193, 108)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 18, 0, 0)
        });

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };
    }

    private async Task StartDownloadAsync()
    {
        if (_cancellation is not null) return;
        if (!Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(this, "Enter a valid HTTP or HTTPS file URL.", "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = _checks.Where(x => x.CheckBox.IsChecked == true).Select(x => x.Path).ToArray();
        if (selected.Length is < 2 or > 5)
        {
            MessageBox.Show(this, "Select between two and five active adapters.", "Select adapters", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (selected.GroupBy(x => x.Gateway).Any(g => g.Count() > 1))
        {
            var answer = MessageBox.Show(this,
                "Some selected adapters use the same default gateway. They may share one router and may not provide additional internet capacity. Continue?",
                "Check independent internet links", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        var destination = _destination.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            BrowseDestination();
            destination = _destination.Text.Trim();
            if (string.IsNullOrWhiteSpace(destination)) return;
        }

        _cancellation = new CancellationTokenSource();
        _start.IsEnabled = false;
        _cancel.IsEnabled = true;
        _progress.Value = 0;
        _progress.IsIndeterminate = true;
        _pathStats.Text = "";
        _status.Text = "Checking byte-range support and opening direct connections through each selected adapter...";
        long lastUiUpdate = 0;

        try
        {
            var result = await LocalMultiWanDownloadService.DownloadAsync(
                uri, destination, selected,
                (done, total) =>
                {
                    var tick = Environment.TickCount64;
                    if (tick - Interlocked.Read(ref lastUiUpdate) < 120 && done < total) return;
                    Interlocked.Exchange(ref lastUiUpdate, tick);
                    Dispatcher.BeginInvoke(() =>
                    {
                        _progress.IsIndeterminate = false;
                        _progress.Value = total > 0 ? Math.Min(100, done * 100d / total) : 0;
                        _status.Text = $"Downloading and combining ranges: {done:N0} / {total:N0} bytes ({(total > 0 ? done * 100d / total : 0):N1}%)";
                    });
                }, _cancellation.Token);

            _progress.IsIndeterminate = false;
            _progress.Value = 100;
            _status.Text = $"Completed: {result.TotalBytes:N0} bytes in {result.Elapsed.TotalSeconds:N1} seconds.";
            _pathStats.Text = "Data downloaded through each adapter: " +
                string.Join("  |  ", result.BytesByAdapter.OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Value:N0} bytes"));
            MessageBox.Show(this,
                $"The VPS-free multi-adapter download completed.\n\nFile: {destination}\nSize: {result.TotalBytes:N0} bytes\nTime: {result.Elapsed.TotalSeconds:N1} seconds",
                "InfraLap Multi-WAN Download", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Download cancelled. Temporary partial ranges were removed.";
        }
        catch (Exception ex)
        {
            _status.Text = "Download failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "InfraLap VPS-free download", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _start.IsEnabled = true;
            _cancel.IsEnabled = false;
            _progress.IsIndeterminate = false;
        }
    }

    private void BrowseDestination()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save downloaded file",
            Filter = "All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(_destination.Text) ? "infralap-download.bin" : Path.GetFileName(_destination.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(_destination.Text))
                ? Path.GetDirectoryName(_destination.Text)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true) _destination.Text = dialog.FileName;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = Brushes.White,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 7, 0, 0)
    };

    private static Button MakeButton(string text, Color color) => new()
    {
        Content = text,
        Padding = new Thickness(14, 9, 14, 9),
        Margin = new Thickness(0, 0, 8, 0),
        Background = new SolidColorBrush(color),
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0),
        FontWeight = FontWeights.SemiBold,
        Cursor = System.Windows.Input.Cursors.Hand
    };
}
'@

$servicePath = Join-Path $services 'LocalMultiWanDownloadService.cs'
$windowPath = Join-Path $app 'LocalMultiWanDownloadWindow.cs'
[IO.File]::WriteAllText($servicePath, $service, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($windowPath, $window, [Text.UTF8Encoding]::new($false))

$bondingPath = Join-Path $app 'BondingWindow.xaml.cs'
$bonding = [IO.File]::ReadAllText($bondingPath)
if (-not $bonding.Contains('Content = "Multi-WAN Download (No VPS)"')) {
    $anchor = 'bar.Children.Add(button);'
    $anchorIndex = $bonding.IndexOf($anchor, [StringComparison]::Ordinal)
    if ($anchorIndex -lt 0) { throw 'Could not locate the existing adapter toolbar button for the local download launcher.' }
    $downloadButton = @'
              var localDownloadButton = new System.Windows.Controls.Button
              {
                  Content = "Multi-WAN Download (No VPS)",
                  Padding = new Thickness(14, 8, 14, 8),
                  Margin = new Thickness(0, 0, 8, 0),
                  FontWeight = FontWeights.SemiBold,
                  Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 132, 92)),
                  Foreground = System.Windows.Media.Brushes.White,
                  BorderThickness = new Thickness(0),
                  Cursor = System.Windows.Input.Cursors.Hand
              };
              localDownloadButton.Click += (_, _) =>
              {
                  var localWindow = new LocalMultiWanDownloadWindow(_paths.ToArray()) { Owner = this };
                  localWindow.ShowDialog();
                  RefreshPaths();
              };
              System.Windows.Controls.DockPanel.SetDock(localDownloadButton, System.Windows.Controls.Dock.Right);
              bar.Children.Add(localDownloadButton);
'@
    $bonding = $bonding.Insert($anchorIndex + $anchor.Length, [Environment]::NewLine + $downloadButton.TrimEnd())
    [IO.File]::WriteAllText($bondingPath, $bonding, [Text.UTF8Encoding]::new($false))
}

if (!(Test-Path $servicePath) -or !(Test-Path $windowPath) -or
    -not $bonding.Contains('Multi-WAN Download (No VPS)') -or
    -not $service.Contains('ConnectCallback') -or
    -not $service.Contains('contentRange.From') -or
    -not $service.Contains('File.Move(mergedPath, fullDestination, overwrite: true)')) {
    throw 'Local VPS-free multi-WAN download source verification failed.'
}
Write-Host 'Added direct, adapter-bound HTTP range downloading with local file reassembly and no relay/VPS.'
