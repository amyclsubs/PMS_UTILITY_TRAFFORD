namespace AmaanParkingSystem.Services
{
    /// <summary>
    /// Singleton in-memory store for the runtime SQL Server connection string.
    /// Thread-safe via lock. Cleared automatically on application restart.
    /// </summary>
    public class ConnectionStringService : IConnectionStringService
    {
        private string? _connectionString;
        private readonly object _lock = new();

        public bool IsConfigured
        {
            get
            {
                lock (_lock)
                {
                    return !string.IsNullOrEmpty(_connectionString);
                }
            }
        }

        public string? GetConnectionString()
        {
            lock (_lock)
            {
                return _connectionString;
            }
        }

        public void SetConnectionString(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));

            lock (_lock)
            {
                _connectionString = connectionString;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _connectionString = null;
            }
        }
    }
}
