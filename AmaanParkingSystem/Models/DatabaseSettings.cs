namespace AmaanParkingSystem.Models
{
    /// <summary>
    /// Represents the persisted database connection settings stored at
    /// C:\ProgramData\AmaanPMS\dbsettings.json
    /// EncryptedPassword is a Base-64 DPAPI-protected blob — never plain text.
    /// </summary>
    public class DatabaseSettings
    {
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;

        /// <summary>Base-64 encoded DPAPI-encrypted password.</summary>
        public string EncryptedPassword { get; set; } = string.Empty;
    }
}