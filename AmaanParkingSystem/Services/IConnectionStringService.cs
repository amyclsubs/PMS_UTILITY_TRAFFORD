namespace AmaanParkingSystem.Services
{
    /// <summary>
    /// Holds the runtime SQL Server connection string configured by the user
    /// via the Database Login page. Lives as a Singleton — cleared on app restart.
    /// </summary>
    public interface IConnectionStringService
    {
        /// <summary>Returns true if a connection string has been set this session.</summary>
        bool IsConfigured { get; }

        /// <summary>Returns the active connection string, or null if not yet set.</summary>
        string? GetConnectionString();

        /// <summary>Stores the validated connection string in memory.</summary>
        void SetConnectionString(string connectionString);

        /// <summary>Clears the stored connection string (used on app-level logout).</summary>
        void Clear();
    }
}
