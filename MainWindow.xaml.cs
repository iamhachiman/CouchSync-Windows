using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using CommunityToolkit.WinUI.Notifications;
using QRCoder;

namespace CouchSync
{
    public class NotificationItem
    {
        public string AppName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
    }

    public partial class MainWindow : Window
    {
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "CouchSync";

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private IntPtr _hwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private string _lastCopiedText = string.Empty;
        private bool _isHandlingIncomingClipboard = false;

        private const string ToastGroup = "CouchSync";

        private readonly AppSessionState _sessionState = SessionStore.Load();
        private readonly ObservableCollection<NotificationItem> _notifications = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly DiscordRelayService _discordRelay = new();
        private readonly bool _startedFromWindowsStartup = WasStartedFromWindowsStartup();

        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private System.Drawing.Icon? _trayOwnedIcon;
        private bool _isExitRequested;
        private bool _hasShownTrayHint;

        private TcpListener? _tcpListener;
        private StreamWriter? _currentClientWriter;
        private string _pairingCode = string.Empty;
        private string _ipAddress = string.Empty;
        private readonly int _port = 50505;

        public MainWindow()
        {
            InitializeComponent();
            NotificationList.ItemsSource = _notifications;
            InitializeTrayIcon();

            if (_startedFromWindowsStartup)
            {
                WindowState = WindowState.Minimized;
            }

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            ToastNotificationManagerCompat.OnActivated += ToastActivated;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureRunOnWindowsStartup();
            ClipboardSyncToggle.IsChecked = _sessionState.SyncClipboard;
            DiscordRelayToggle.IsChecked = _sessionState.DiscordRelayEnabled;
            DiscordBotTokenBox.Password = _sessionState.DiscordBotToken;
            DiscordUserIdTextBox.Text = _sessionState.DiscordTargetUserId;
            ConfigureDiscordRelay();

            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(HwndHook);
            AddClipboardFormatListener(_hwnd);

            InitializeServer();
            ApplyShellState(isConnected: false, detail: _sessionState.HasTrustedDevice ? "Waiting for your phone to reconnect automatically." : "Open the Android app and scan the QR code.", deviceName: _sessionState.TrustedDeviceName);
            GenerateQrCode();
            RefreshNotificationSummary();

            if (_startedFromWindowsStartup)
            {
                HideToTray(showHint: false);
            }

            await StartListeningAsync();
        }

        private void InitializeTrayIcon()
        {
            _trayOwnedIcon = TryGetTrayIcon();
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "CouchSync",
                Icon = _trayOwnedIcon ?? System.Drawing.SystemIcons.Application,
                Visible = true
            };

            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            var openItem = new System.Windows.Forms.ToolStripMenuItem("Open CouchSync");
            openItem.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);

            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) => Dispatcher.Invoke(ExitApplicationFromTray);

            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            contextMenu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = contextMenu;
        }

        private static System.Drawing.Icon? TryGetTrayIcon()
        {
            try
            {
                string? processPath = Environment.ProcessPath;
                return string.IsNullOrWhiteSpace(processPath)
                    ? null
                    : System.Drawing.Icon.ExtractAssociatedIcon(processPath);
            }
            catch
            {
                return null;
            }
        }

        private void HideToTray(bool showHint)
        {
            ShowInTaskbar = false;
            Hide();

            if (showHint && !_hasShownTrayHint)
            {
                _trayIcon?.ShowBalloonTip(
                    2400,
                    "CouchSync is still running",
                    "Double-click the tray icon to reopen the window.",
                    System.Windows.Forms.ToolTipIcon.Info);
                _hasShownTrayHint = true;
            }
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Focus();
        }

        private void ExitApplicationFromTray()
        {
            _isExitRequested = true;
            Close();
        }

        private static bool WasStartedFromWindowsStartup()
        {
            return Environment.GetCommandLineArgs()
                .Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        }

        private void EnsureRunOnWindowsStartup()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return;
                }

                string startupCommand = $"\"{exePath}\" --startup";

                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true);

                if (key == null)
                {
                    return;
                }

                string? existingCommand = key.GetValue(StartupValueName) as string;
                if (!string.Equals(existingCommand, startupCommand, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(StartupValueName, startupCommand, RegistryValueKind.String);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Startup registration failed: {ex.Message}");
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExitRequested)
            {
                e.Cancel = true;
                HideToTray(showHint: true);
                return;
            }

            if (_hwnd != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(_hwnd);
                _hwndSource?.RemoveHook(HwndHook);
            }

            ToastNotificationManagerCompat.OnActivated -= ToastActivated;
            _cts.Cancel();
            _tcpListener?.Stop();
            _discordRelay.Dispose();

            if (_trayIcon != null)
            {
                var menu = _trayIcon.ContextMenuStrip;
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
                menu?.Dispose();
            }

            _trayOwnedIcon?.Dispose();
            _trayOwnedIcon = null;
        }

        private void InitializeServer()
        {
            _ipAddress = GetLocalIPAddress();
            _pairingCode = GetOrCreatePairingCode();
            PairingCodeText.Text = _pairingCode;
            NetworkStatusText.Text = $"Listening on {_ipAddress}:{_port}";
            SessionStore.Save(_sessionState);
        }

        private string GetOrCreatePairingCode()
        {
            if (_sessionState.PairingCode.Length == 4 && _sessionState.PairingCode.All(char.IsDigit))
            {
                return _sessionState.PairingCode;
            }

            var code = Random.Shared.Next(1000, 9999).ToString();
            _sessionState.PairingCode = code;
            return code;
        }

        private string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            return "127.0.0.1";
        }

        private void GenerateQrCode()
        {
            string payload = JsonSerializer.Serialize(new
            {
                ip = _ipAddress,
                port = _port,
                code = _pairingCode,
                deviceName = Environment.MachineName
            });

            using QRCodeGenerator generator = new();
            using QRCodeData qrCodeData = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            using BitmapByteQRCode qrCode = new(qrCodeData);
            byte[] image = qrCode.GetGraphic(20);
            using MemoryStream stream = new(image);

            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            QrCodeImage.Source = bitmap;
        }

        private async Task StartListeningAsync()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, _port);
                _tcpListener.Start();

                while (!_cts.Token.IsCancellationRequested)
                {
                    var client = await _tcpListener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleClientAsync(client);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Dispatcher.Invoke(() => NetworkStatusText.Text = $"Listener error: {ex.Message}");
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            bool authenticated = false;
            string currentDeviceName = _sessionState.TrustedDeviceName;

            try
            {
                using NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.UTF8);
                using StreamWriter writer = new(stream, Encoding.UTF8) { AutoFlush = true };
                _currentClientWriter = writer;

                Dispatcher.Invoke(() => ApplyShellState(false, "Phone connecting...", currentDeviceName));

                while (client.Connected && !_cts.Token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(_cts.Token);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        break;
                    }

                    try
                    {
                        JsonNode? node = JsonNode.Parse(line);
                        if (node == null)
                        {
                            continue;
                        }

                        string type = node["type"]?.ToString() ?? string.Empty;
                        switch (type)
                        {
                            case "pair":
                                {
                                    string code = node["code"]?.ToString() ?? string.Empty;
                                    if (code != _pairingCode)
                                    {
                                        await writer.WriteLineAsync("{\"type\":\"rejected\"}");
                                        return;
                                    }

                                    authenticated = true;
                                    currentDeviceName = node["deviceName"]?.ToString() ?? "Android phone";
                                    _sessionState.TrustedDeviceName = currentDeviceName;
                                    SessionStore.Save(_sessionState);
                                    await writer.WriteLineAsync("{\"type\":\"paired\"}");

                                    Dispatcher.Invoke(() => ApplyShellState(true, "Connected and syncing in real time.", currentDeviceName));
                                    break;
                                }
                            case "notification" when authenticated:
                                HandleIncomingNotification(node);
                                break;
                            case "notification_removed" when authenticated:
                                HandleRemovedNotification(node);
                                break;
                            case "clipboard" when authenticated:
                                HandleIncomingClipboard(node);
                                break;
                            case "ping":
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"JSON Error: {ex.Message} on line: {line}");
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _currentClientWriter = null;
                client.Close();
                Dispatcher.Invoke(() => ApplyShellState(false, "Waiting for your phone to reconnect automatically.", currentDeviceName));
            }
        }

        private void HandleIncomingNotification(JsonNode node)
        {
            string app = node["app"]?.ToString() ?? "Unknown App";
            string title = node["title"]?.ToString() ?? string.Empty;
            string text = node["text"]?.ToString() ?? string.Empty;
            bool isHistoric = string.Equals(node["historic"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            string key = node["key"]?.ToString() ?? string.Empty;

            Dispatcher.Invoke(() =>
            {
                var existing = _notifications.FirstOrDefault(item => item.Key == key && !string.IsNullOrWhiteSpace(key));
                if (existing != null)
                {
                    _notifications.Remove(existing);
                }

                _notifications.Insert(0, new NotificationItem
                {
                    AppName = app,
                    Title = title,
                    Content = text,
                    Timestamp = DateTime.Now.ToString("HH:mm"),
                    Key = key
                });

                RefreshNotificationSummary();

                if (!isHistoric)
                {
                    ShowToast(app, title, text, key);
                }
            });

            if (!isHistoric)
            {
                _ = RelayNotificationToDiscordAsync(app, title, text);
            }
        }

        private async Task RelayNotificationToDiscordAsync(string app, string title, string text)
        {
            if (!_sessionState.DiscordRelayEnabled)
            {
                return;
            }

            if (!_discordRelay.HasValidConfiguration())
            {
                Dispatcher.Invoke(() => DiscordRelayStatusText.Text = "Discord relay enabled but bot token/user ID is invalid.");
                return;
            }

            try
            {
                var result = await _discordRelay.SendNotificationAsync(app, title, text, _cts.Token);
                Dispatcher.Invoke(() =>
                {
                    DiscordRelayStatusText.Text = result.Success
                        ? $"Discord sent at {DateTime.Now:HH:mm:ss}."
                        : $"Discord error: {result.Message}";
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => DiscordRelayStatusText.Text = $"Discord error: {ex.Message}");
            }
        }

        private void HandleRemovedNotification(JsonNode node)
        {
            string key = node["key"]?.ToString() ?? string.Empty;
            string app = node["app"]?.ToString() ?? string.Empty;
            string title = node["title"]?.ToString() ?? string.Empty;
            string text = node["text"]?.ToString() ?? string.Empty;

            Dispatcher.Invoke(() =>
            {
                NotificationItem? match = null;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    match = _notifications.FirstOrDefault(item => item.Key == key);
                }

                match ??= _notifications.FirstOrDefault(item =>
                    item.AppName == app && item.Title == title && item.Content == text);

                if (match != null)
                {
                    _notifications.Remove(match);
                    RemoveToastFromActionCenter(match.Key);
                    RefreshNotificationSummary();
                }
            });
        }

        private void ShowToast(string app, string title, string text, string key)
        {
            string tag = SanitizeToastTag(key);

            var builder = new ToastContentBuilder()
                .AddArgument("action", "viewNotification")
                .AddArgument("key", key)
                .AddText(app)
                .AddText(string.IsNullOrWhiteSpace(title) ? "New notification" : title);

            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.AddText(text);
            }

            var toast = new Windows.UI.Notifications.ToastNotification(builder.GetToastContent().GetXml())
            {
                Tag = tag,
                Group = ToastGroup
            };

            toast.Dismissed += (_, e) =>
            {
                if (e.Reason == Windows.UI.Notifications.ToastDismissalReason.UserCanceled)
                {
                    Dispatcher.Invoke(() => RemoveNotificationEverywhere(key, propagateToPhone: true));
                }
            };

            ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
        }

        private void RemoveToastFromActionCenter(string key)
        {
            try
            {
                ToastNotificationManagerCompat.History.Remove(SanitizeToastTag(key), ToastGroup);
            }
            catch
            {
            }
        }

        private static string SanitizeToastTag(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return Guid.NewGuid().ToString("N")[..16];
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash)[..16];
        }

        private void ToastActivated(ToastNotificationActivatedEventArgsCompat e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var args = ToastArguments.Parse(e.Argument);
                    args.TryGetValue("key", out string key);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        RemoveNotificationEverywhere(key, propagateToPhone: true);
                    }
                }
                catch
                {
                }

                RestoreFromTray();
            });
        }

        private void ClearAlerts_Click(object sender, RoutedEventArgs e)
        {
            _notifications.Clear();
            ToastNotificationManagerCompat.History.RemoveGroup(ToastGroup);
            RefreshNotificationSummary();
            TrySendToPhone("{\"type\":\"clear_all\"}");
        }

        private void ReconnectManually_Click(object sender, RoutedEventArgs e)
        {
            _sessionState.TrustedDeviceName = string.Empty;
            _sessionState.PairingCode = Random.Shared.Next(1000, 9999).ToString();
            SessionStore.Save(_sessionState);

            _pairingCode = _sessionState.PairingCode;
            PairingCodeText.Text = _pairingCode;
            GenerateQrCode();

            ApplyShellState(
                isConnected: false,
                detail: "Open the Android app and scan the new QR code.",
                deviceName: string.Empty
            );
        }

        private void DismissNotification_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is NotificationItem item)
            {
                RemoveNotificationEverywhere(item.Key, propagateToPhone: true);
            }
        }

        private void RemoveNotificationEverywhere(string key, bool propagateToPhone)
        {
            NotificationItem? item = _notifications.FirstOrDefault(entry => entry.Key == key);
            if (item != null)
            {
                _notifications.Remove(item);
            }

            RemoveToastFromActionCenter(key);
            RefreshNotificationSummary();

            if (propagateToPhone)
            {
                string escapedKey = JsonSerializer.Serialize(key);
                TrySendToPhone($"{{\"type\":\"clear\",\"key\":{escapedKey}}}");
            }
        }

        private void TrySendToPhone(string payload)
        {
            try
            {
                _currentClientWriter?.WriteLine(payload);
            }
            catch
            {
            }
        }

        private void ClipboardSyncToggle_Click(object sender, RoutedEventArgs e)
        {
            _sessionState.SyncClipboard = ClipboardSyncToggle.IsChecked == true;
            SessionStore.Save(_sessionState);
        }

        private void DiscordRelayToggle_Click(object sender, RoutedEventArgs e)
        {
            SaveDiscordSettingsFromUi();
        }

        private void SaveDiscordRelay_Click(object sender, RoutedEventArgs e)
        {
            SaveDiscordSettingsFromUi();
        }

        private async void TestDiscordRelay_Click(object sender, RoutedEventArgs e)
        {
            SaveDiscordSettingsFromUi();

            if (!_sessionState.DiscordRelayEnabled)
            {
                DiscordRelayStatusText.Text = "Enable Discord relay first.";
                return;
            }

            if (!_discordRelay.HasValidConfiguration())
            {
                DiscordRelayStatusText.Text = "Enter a valid bot token and numeric Discord user ID.";
                return;
            }

            DiscordRelayStatusText.Text = "Sending Discord test message...";

            try
            {
                var result = await _discordRelay.SendTestMessageAsync(_cts.Token);
                DiscordRelayStatusText.Text = result.Success
                    ? "Discord test sent successfully."
                    : $"Discord test failed: {result.Message}";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DiscordRelayStatusText.Text = $"Discord test failed: {ex.Message}";
            }
        }

        private void SaveDiscordSettingsFromUi()
        {
            _sessionState.DiscordRelayEnabled = DiscordRelayToggle.IsChecked == true;
            _sessionState.DiscordBotToken = DiscordBotTokenBox.Password.Trim();
            _sessionState.DiscordTargetUserId = DiscordUserIdTextBox.Text.Trim();
            SessionStore.Save(_sessionState);
            ConfigureDiscordRelay();
        }

        private void ConfigureDiscordRelay()
        {
            _discordRelay.Configure(_sessionState.DiscordBotToken, _sessionState.DiscordTargetUserId);

            if (!_sessionState.DiscordRelayEnabled)
            {
                DiscordRelayStatusText.Text = "Discord relay is off.";
                return;
            }

            DiscordRelayStatusText.Text = _discordRelay.HasValidConfiguration()
                ? "Discord relay is ready."
                : "Enter bot token and numeric Discord user ID.";
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE && _sessionState.SyncClipboard)
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    if (_isHandlingIncomingClipboard) return;

                    try
                    {
                        if (System.Windows.Clipboard.ContainsText())
                        {
                            string text = System.Windows.Clipboard.GetText();
                            if (!string.IsNullOrEmpty(text) && text != _lastCopiedText)
                            {
                                _lastCopiedText = text;
                                string safeText = JsonSerializer.Serialize(text);
                                TrySendToPhone($"{{\"type\":\"clipboard\",\"text\":{safeText}}}");
                            }
                        }
                    }
                    catch { }
                });
            }
            return IntPtr.Zero;
        }

        private void HandleIncomingClipboard(JsonNode node)
        {
            string text = node["text"]?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(text) || !_sessionState.SyncClipboard) return;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (text != _lastCopiedText)
                    {
                        _isHandlingIncomingClipboard = true;

                        if (TrySetClipboardTextWithRetry(text))
                        {
                            _lastCopiedText = text;
                            System.Diagnostics.Debug.WriteLine($"Clipboard updated from phone ({text.Length} chars)");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("Failed to set clipboard from phone after retries.");
                        }
                    }
                }
                catch { }
                finally
                {
                    _isHandlingIncomingClipboard = false;
                }
            });
        }

        private static bool TrySetClipboardTextWithRetry(string text)
        {
            const int maxAttempts = 5;
            const int delayMs = 60;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    return true;
                }
                catch (COMException)
                {
                    if (attempt == maxAttempts)
                    {
                        return false;
                    }

                    Thread.Sleep(delayMs);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private void ApplyShellState(bool isConnected, string detail, string? deviceName)
        {
            bool showDashboard = _sessionState.HasTrustedDevice || !string.IsNullOrWhiteSpace(deviceName);
            PairingScreen.Visibility = showDashboard ? Visibility.Collapsed : Visibility.Visible;
            MainScreen.Visibility = showDashboard ? Visibility.Visible : Visibility.Collapsed;

            NetworkStatusText.Text = _sessionState.HasTrustedDevice
                ? $"Listening on {_ipAddress}:{_port}. Trusted device saved."
                : $"Listening on {_ipAddress}:{_port}";

            if (!showDashboard)
            {
                return;
            }

            ConnectedDeviceNameText.Text = string.IsNullOrWhiteSpace(deviceName)
                ? (_sessionState.HasTrustedDevice ? _sessionState.TrustedDeviceName : "Android phone")
                : deviceName;
            ConnectionStateText.Text = isConnected ? "Connected" : "Waiting for reconnect";
            ConnectionDetailText.Text = detail;
            ConnectionStateDot.Fill = CreateBrush(isConnected ? "#8FA785" : "#C89A5D");
            ConnectionStatePill.Background = CreateBrush(isConnected ? "#2A8FA785" : "#2AC89A5D");
            ReconnectManuallyButton.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RefreshNotificationSummary()
        {
            NotificationCountText.Text = _notifications.Count switch
            {
                1 => "1 alert",
                _ => $"{_notifications.Count} alerts"
            };
            EmptyStatePanel.Visibility = _notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static SolidColorBrush CreateBrush(string hex)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        }
    }
}
