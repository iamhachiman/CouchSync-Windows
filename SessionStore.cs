using System;
using System.IO;
using System.Text.Json;

namespace CouchSync
{
    public sealed class AppSessionState
    {
        public string PairingCode { get; set; } = string.Empty;
        public string TrustedDeviceName { get; set; } = string.Empty;

        public bool HasTrustedDevice => !string.IsNullOrWhiteSpace(TrustedDeviceName);
    }

    public static class SessionStore
    {
        private static readonly string SessionFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CouchSync",
            "session.json");

        public static AppSessionState Load()
        {
            try
            {
                if (!File.Exists(SessionFilePath))
                {
                    return new AppSessionState();
                }

                var json = File.ReadAllText(SessionFilePath);
                return JsonSerializer.Deserialize<AppSessionState>(json) ?? new AppSessionState();
            }
            catch
            {
                return new AppSessionState();
            }
        }

        public static void Save(AppSessionState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionFilePath)!);
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SessionFilePath, json);
        }
    }
}
