using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
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
        private const string ToastGroup = "CouchSync";

        private TcpListener? _tcpListener;
        private StreamWriter? _currentClientWriter;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private ObservableCollection<NotificationItem> _notifications = new ObservableCollection<NotificationItem>();
        private string _pairingCode = string.Empty;
        private string _ipAddress = string.Empty;
        private int _port = 50505;

        public MainWindow()
        {
            InitializeComponent();
            NotificationList.ItemsSource = _notifications;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            // Subscribe to toast activation/dismissed callbacks from Windows Action Center
            ToastNotificationManagerCompat.OnActivated += ToastActivated;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeServer();
            GenerateQrCode();
            await StartListeningAsync();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _cts.Cancel();
            _tcpListener?.Stop();
        }

        private void InitializeServer()
        {
            _ipAddress = GetLocalIPAddress();
            Random rng = new Random();
            _pairingCode = rng.Next(1000, 9999).ToString();
            PairingCodeText.Text = _pairingCode;
            NetworkStatusText.Text = $"Listening on {_ipAddress}:{_port}\nWaiting for connection...";
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
            string payload = $"{{\"ip\":\"{_ipAddress}\",\"port\":{_port},\"code\":\"{_pairingCode}\"}}";
            using (QRCodeGenerator qrGenerator = new QRCodeGenerator())
            using (QRCodeData qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q))
            using (BitmapByteQRCode qrCode = new BitmapByteQRCode(qrCodeData))
            {
                byte[] qrCodeImage = qrCode.GetGraphic(20);
                using (MemoryStream stream = new MemoryStream(qrCodeImage))
                {
                    BitmapImage bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = stream;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    QrCodeImage.Source = bitmapImage;
                }
            }
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
                Dispatcher.Invoke(() => NetworkStatusText.Text = $"Error: {ex.Message}");
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                bool authenticated = false;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                _currentClientWriter = writer;

                Dispatcher.Invoke(() => NetworkStatusText.Text = "Client connecting...");

                while (client.Connected && !_cts.Token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(_cts.Token);
                    if (string.IsNullOrEmpty(line)) break;

                    try
                    {
                        var node = System.Text.Json.Nodes.JsonNode.Parse(line);
                        if (node != null)
                        {
                            string? type = node["type"]?.ToString();
                            if (type == "pair")
                            {
                                string? code = node["code"]?.ToString();
                                if (code == _pairingCode)
                                {
                                    authenticated = true;
                                    // Send ack so Android reader loop gets a valid JSON line
                                    await writer.WriteLineAsync("{\"type\":\"paired\"}");
                                    Dispatcher.Invoke(() => 
                                    {
                                        NetworkStatusText.Text = "Connected";
                                        PairingScreen.Visibility = Visibility.Collapsed;
                                        MainScreen.Visibility = Visibility.Visible;
                                    });
                                }
                                else
                                {
                                    await writer.WriteLineAsync("{\"type\":\"rejected\"}");
                                    break; // wrong code
                                }
                            }
                            else if (type == "notification" && authenticated)
                            {
                                string app = node["app"]?.ToString() ?? "Unknown App";
                                string title = node["title"]?.ToString() ?? "";
                                string text = node["text"]?.ToString() ?? "";
                                bool isHistoric = node["historic"]?.ToString() == "true";
                                
                                Dispatcher.Invoke(() =>
                                {
                                    _notifications.Insert(0, new NotificationItem
                                    {
                                        AppName = app,
                                        Title = title,
                                        Content = text,
                                        Timestamp = DateTime.Now.ToString("HH:mm"),
                                        Key = node["key"]?.ToString() ?? ""
                                    });
                                    
                                    if (!isHistoric)
                                    {
                                        ShowToast(app, title, text, node["key"]?.ToString() ?? "");
                                    }
                                });
                            }
                            else if (type == "notification_removed" && authenticated)
                            {
                                string key = node["key"]?.ToString() ?? "";
                                
                                Dispatcher.Invoke(() =>
                                {
                                    NotificationItem? toRemove = null;
                                    if (!string.IsNullOrEmpty(key))
                                    {
                                        // Prefer matching by unique key (most reliable)
                                        toRemove = _notifications.FirstOrDefault(n => n.Key == key);
                                    }
                                    if (toRemove == null)
                                    {
                                        // Fallback: match by app+title+text if key is missing
                                        string app = node["app"]?.ToString() ?? "Unknown App";
                                        string title = node["title"]?.ToString() ?? "";
                                        string text = node["text"]?.ToString() ?? "";
                                        toRemove = _notifications.FirstOrDefault(n => n.AppName == app && n.Title == title && n.Content == text);
                                    }
                                    if (toRemove != null)
                                    {
                                        _notifications.Remove(toRemove);
                                        // Also remove from Windows Action Center
                                        RemoveToastFromActionCenter(toRemove.Key);
                                    }
                                });
                            }
                            else if (type == "ping")
                            {
                                // Handled safely to keep TCP connection alive
                            }
                        }
                    }
                    catch (Exception ex) 
                    { 
                        System.Diagnostics.Debug.WriteLine($"JSON Error: {ex.Message} on line: {line}"); 
                    }
                }
            }
            catch { }
            finally
            {
                _currentClientWriter = null;
                client.Close();
                Dispatcher.Invoke(() => 
                {
                    NetworkStatusText.Text = "Waiting for connection...";
                    PairingScreen.Visibility = Visibility.Visible;
                    MainScreen.Visibility = Visibility.Collapsed;
                });
            }
        }

        private void ShowToast(string app, string title, string text, string key)
        {
            // Use the notification key as the toast tag so we can look it up later
            string tag = SanitizeToastTag(key);

            var builder = new ToastContentBuilder()
                .AddArgument("action", "viewNotification")
                .AddArgument("key", key)
                .AddText(app)
                .AddText(title);

            if (!string.IsNullOrEmpty(text))
                builder.AddText(text);

            // Build a raw ToastNotification so we can set tag/group and wire up events
            var toastContent = builder.GetToastContent();
            var toast = new Windows.UI.Notifications.ToastNotification(toastContent.GetXml())
            {
                Tag   = tag,
                Group = ToastGroup
            };

            // When the user dismisses the toast from Action Center → sync back
            toast.Dismissed += (s, e) =>
            {
                // UserCanceled means the user explicitly swiped/clicked the X in Action Center
                if (e.Reason == Windows.UI.Notifications.ToastDismissalReason.UserCanceled)
                {
                    Dispatcher.Invoke(() =>
                    {
                        var item = _notifications.FirstOrDefault(n => n.Key == key);
                        if (item != null)
                        {
                            _notifications.Remove(item);
                            // Tell the mobile app to dismiss this notification too
                            try { _currentClientWriter?.WriteLine($"{{\"type\":\"clear\",\"key\":\"{key}\"}}" ); } catch { }
                        }
                    });
                }
            };

            ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
        }

        /// <summary>
        /// Removes a single toast from the Windows Action Center by its notification key.
        /// </summary>
        private void RemoveToastFromActionCenter(string key)
        {
            try
            {
                string tag = SanitizeToastTag(key);
                ToastNotificationManagerCompat.History.Remove(tag, ToastGroup);
            }
            catch { }
        }

        /// <summary>
        /// Toast tags must be ≤ 16 characters, alphanumeric only.
        /// We use the first 16 chars of a hex-encoded key.
        /// </summary>
        private static string SanitizeToastTag(string key)
        {
            if (string.IsNullOrEmpty(key)) return Guid.NewGuid().ToString("N")[..16];
            // Use a short hash so the tag is always valid but still unique
            int hash = Math.Abs(key.GetHashCode());
            return hash.ToString()[..Math.Min(hash.ToString().Length, 16)];
        }

        /// <summary>
        /// Handles toast activation (user tapping the toast body in Action Center).
        /// </summary>
        private void ToastActivated(ToastNotificationActivatedEventArgsCompat e)
        {
            // Bring the window to the foreground when a toast is clicked
            Dispatcher.Invoke(() =>
            {
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
                Focus();
            });
        }

        private void ClearAlerts_Click(object sender, RoutedEventArgs e)
        {
            _notifications.Clear();
            // Remove all CouchSync toasts from Action Center
            ToastNotificationManagerCompat.History.RemoveGroup(ToastGroup);
            try { _currentClientWriter?.WriteLine("{\"type\":\"clear_all\"}"); } catch {}
        }

        private void DismissNotification_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is NotificationItem item)
            {
                _notifications.Remove(item);
                // Remove corresponding toast from Action Center
                RemoveToastFromActionCenter(item.Key);
                try { _currentClientWriter?.WriteLine($"{{\"type\":\"clear\",\"key\":\"{item.Key}\"}}"); } catch {}
            }
        }
    }
}