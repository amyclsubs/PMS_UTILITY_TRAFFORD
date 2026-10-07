using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmaanParkingSystem.Models;

namespace AmaanParkingSystem.Services
{
    /// <summary>
    /// Persists database connection settings to a local JSON file at
    /// C:\ProgramData\AmaanPMS\dbsettings.json.
    /// Passwords are encrypted with Windows DPAPI (ProtectedData) so the
    /// file is useless to anyone who copies it off the machine.
    /// </summary>
    public interface IDatabaseSettingsService
    {
        /// <summary>Loads saved settings. Returns null when no file exists yet.</summary>
        DatabaseSettings? Load();

        /// <summary>
        /// Saves settings, encrypting the plain-text password before writing.
        /// </summary>
        void Save(string serverName, string databaseName, string username, string plainPassword);
    }

    public class DatabaseSettingsService : IDatabaseSettingsService
    {
        // ── Storage path ────────────────────────────────────────────────────
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AmaanPMS",
            "dbsettings.json");

        private readonly ILogger<DatabaseSettingsService> _logger;

        public DatabaseSettingsService(ILogger<DatabaseSettingsService> logger)
        {
            _logger = logger;
        }

        // ── Load ─────────────────────────────────────────────────────────────
        public DatabaseSettings? Load()
        {
            if (!File.Exists(SettingsFilePath))
                return null;

            try
            {
                string json = File.ReadAllText(SettingsFilePath);
                return JsonSerializer.Deserialize<DatabaseSettings>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read DB settings from {Path}.", SettingsFilePath);
                return null;
            }
        }

        // ── Save ─────────────────────────────────────────────────────────────
        public void Save(string serverName, string databaseName, string username, string plainPassword)
        {
            try
            {
                // Ensure directory exists
                string? directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var settings = new DatabaseSettings
                {
                    ServerName = serverName,
                    DatabaseName = databaseName,
                    Username = username,
                    EncryptedPassword = EncryptPassword(plainPassword)
                };

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(SettingsFilePath, json);
                _logger.LogInformation("DB settings saved to {Path}.", SettingsFilePath);
            }
            catch (Exception ex)
            {
                // Non-fatal — app continues even if settings cannot be saved.
                _logger.LogWarning(ex, "Failed to save DB settings to {Path}.", SettingsFilePath);
            }
        }

        // ── Decrypt helper (used by controller to recover plain-text) ─────────
        /// <summary>
        /// Decrypts a Base-64 encoded DPAPI-encrypted password back to plain text.
        /// Returns an empty string if decryption fails.
        /// </summary>
        public static string DecryptPassword(string encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64))
                return string.Empty;

            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(encryptedBase64);
                byte[] plainBytes = ProtectedData.Unprotect(
                    encryptedBytes,
                    null,
                    DataProtectionScope.LocalMachine);

                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                // If decryption fails (e.g. file moved from another machine) return empty.
                return string.Empty;
            }
        }

        // ── Encrypt helper (private) ──────────────────────────────────────────
        private static string EncryptPassword(string plainPassword)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainPassword);
            byte[] encryptedBytes = ProtectedData.Protect(
                plainBytes,
                null,
                DataProtectionScope.LocalMachine);

            return Convert.ToBase64String(encryptedBytes);
        }
    }
}
