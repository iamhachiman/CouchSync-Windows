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
                                    Dispatcher.Invoke(() => 
                                    {
                                        NetworkStatusText.Text = "Waiting for connection..."; // reset for later
                                        PairingScreen.Visibility = Visibility.Collapsed;
                                        MainScreen.Visibility = Visibility.Visible;
                                    });
                                }
                                else
                                {
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
                                        ShowToast(app, title, text);
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

        private void ShowToast(string app, string title, string text)
        {
            new ToastContentBuilder()
                .AddArgument("action", "viewConversation")
                .AddText(app)
                .AddText(title)
                .AddText(text)
                .Show();
        }

        private void ClearAlerts_Click(object sender, RoutedEventArgs e)
        {
            _notifications.Clear();
            ToastNotificationManagerCompat.History.Clear();
            try { _currentClientWriter?.WriteLine("{\"type\":\"clear_all\"}"); } catch {}
        }

        private void DismissNotification_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is NotificationItem item)
            {
                _notifications.Remove(item);
                try { _currentClientWriter?.WriteLine($"{{\"type\":\"clear\",\"key\":\"{item.Key}\"}}"); } catch {}
            }
        }
    }
}