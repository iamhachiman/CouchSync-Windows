using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CouchSync
{
    internal sealed class DiscordRelayService : IDisposable
    {
        private static readonly Uri DiscordApiBaseUri = new("https://discord.com/api/v10/");

        private readonly HttpClient _httpClient = new() { BaseAddress = DiscordApiBaseUri };
        private readonly SemaphoreSlim _channelLock = new(1, 1);

        private string _botToken = string.Empty;
        private string _targetUserId = string.Empty;
        private string? _dmChannelId;

        public void Configure(string botToken, string targetUserId)
        {
            string normalizedToken = botToken.Trim();
            string normalizedUserId = targetUserId.Trim();

            bool changed = !string.Equals(_botToken, normalizedToken, StringComparison.Ordinal)
                || !string.Equals(_targetUserId, normalizedUserId, StringComparison.Ordinal);

            _botToken = normalizedToken;
            _targetUserId = normalizedUserId;

            if (changed)
            {
                _dmChannelId = null;
            }
        }

        public bool HasValidConfiguration()
        {
            return !string.IsNullOrWhiteSpace(_botToken)
                && !string.IsNullOrWhiteSpace(_targetUserId)
                && ulong.TryParse(_targetUserId, out _);
        }

        public Task<(bool Success, string Message)> SendTestMessageAsync(CancellationToken cancellationToken)
        {
            string message = $"CouchSync test from {Environment.MachineName} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.";
            return SendRawMessageAsync(message, cancellationToken);
        }

        public Task<(bool Success, string Message)> SendNotificationAsync(
            string app,
            string title,
            string content,
            CancellationToken cancellationToken)
        {
            string safeTitle = string.IsNullOrWhiteSpace(title) ? "New notification" : title.Trim();
            string safeContent = string.IsNullOrWhiteSpace(content) ? string.Empty : content.Trim();
            string body = string.IsNullOrWhiteSpace(safeContent)
                ? $"[{app}] {safeTitle}"
                : $"[{app}] {safeTitle}\n{safeContent}";

            return SendRawMessageAsync(body, cancellationToken);
        }

        private async Task<(bool Success, string Message)> SendRawMessageAsync(string message, CancellationToken cancellationToken)
        {
            if (!HasValidConfiguration())
            {
                return (false, "Discord bot token or user ID is missing/invalid.");
            }

            string? channelId = await GetOrCreateDmChannelAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return (false, "Unable to open a DM channel with that Discord user.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/messages");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _botToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { content = TrimMessage(message) }),
                Encoding.UTF8,
                "application/json"
            );

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Message sent to Discord.");
            }

            string apiError = await response.Content.ReadAsStringAsync(cancellationToken);
            return (false, $"Discord API error {(int)response.StatusCode}: {apiError}");
        }

        private async Task<string?> GetOrCreateDmChannelAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_dmChannelId))
            {
                return _dmChannelId;
            }

            await _channelLock.WaitAsync(cancellationToken);
            try
            {
                if (!string.IsNullOrWhiteSpace(_dmChannelId))
                {
                    return _dmChannelId;
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, "users/@me/channels");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _botToken);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { recipient_id = _targetUserId }),
                    Encoding.UTF8,
                    "application/json"
                );

                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                using JsonDocument document = JsonDocument.Parse(json);
                _dmChannelId = document.RootElement.GetProperty("id").GetString();
                return _dmChannelId;
            }
            catch
            {
                return null;
            }
            finally
            {
                _channelLock.Release();
            }
        }

        private static string TrimMessage(string message)
        {
            const int maxDiscordLength = 1900;
            if (message.Length <= maxDiscordLength)
            {
                return message;
            }

            return message[..maxDiscordLength];
        }

        public void Dispose()
        {
            _channelLock.Dispose();
            _httpClient.Dispose();
        }
    }
}
