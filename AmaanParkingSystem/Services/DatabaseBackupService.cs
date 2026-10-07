using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Services
{
    public class DatabaseBackupService
    {
        private readonly IConnectionStringService _connStrService;
        private readonly ILogger<DatabaseBackupService> _logger;
        private readonly string _backupFolder;

        public DatabaseBackupService(
            IConnectionStringService connStrService,
            IConfiguration configuration,
            ILogger<DatabaseBackupService> logger)
        {
            _connStrService = connStrService;
            _logger = logger;
            _backupFolder = configuration["BackupSettings:BackupFolder"] ?? @"C:\Payment Website\Database Bak";
        }

        public async Task<(bool success, string message, string filePath)> CreateBackupAsync()
        {
            try
            {
                // Ensure backup folder exists
                if (!Directory.Exists(_backupFolder))
                {
                    Directory.CreateDirectory(_backupFolder);
                    _logger.LogInformation($"Created backup folder: {_backupFolder}");
                }

                // Generate backup filename with timestamp
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var backupFileName = $"AMAAN_PMS_Backup_{timestamp}.bak";
                var backupFilePath = Path.Combine(_backupFolder, backupFileName);

                // ✅ UPDATED: Removed COMPRESSION for SQL Server Express
                var backupQuery = $@"
            BACKUP DATABASE [AMAAN_PMS] 
            TO DISK = @BackupPath 
            WITH FORMAT, 
                 MEDIANAME = 'AMAAN_PMS_Backup',
                 NAME = 'Full Backup of AMAAN_PMS';";

                using (var connection = new SqlConnection(_connStrService.GetConnectionString() ?? throw new InvalidOperationException("Database connection string is not configured.")))
                {
                    await connection.OpenAsync();

                    using (var command = new SqlCommand(backupQuery, connection))
                    {
                        command.CommandTimeout = 600; // 10 minutes timeout
                        command.Parameters.AddWithValue("@BackupPath", backupFilePath);

                        _logger.LogInformation($"Starting database backup to: {backupFilePath}");

                        await command.ExecuteNonQueryAsync();

                        _logger.LogInformation($"Database backup completed successfully: {backupFilePath}");
                    }
                }

                // Get file size
                var fileInfo = new FileInfo(backupFilePath);
                var fileSizeMB = fileInfo.Length / (1024.0 * 1024.0);

                return (true, $"Backup completed successfully. File: {backupFileName} ({fileSizeMB:F2} MB)", backupFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Database backup failed: {ex.Message}");
                return (false, $"Backup failed: {ex.Message}", null);
            }
        }


        public async Task<(bool success, string message, List<string> backups)> GetBackupHistoryAsync()
        {
            try
            {
                if (!Directory.Exists(_backupFolder))
                {
                    return (false, "Backup folder does not exist", new List<string>());
                }

                var backupFiles = Directory.GetFiles(_backupFolder, "AMAAN_PMS_Backup_*.bak")
                    .OrderByDescending(f => new FileInfo(f).CreationTime)
                    .Take(20)
                    .Select(f => new FileInfo(f))
                    .Select(fi => new
                    {
                        FileName = fi.Name,
                        Size = $"{fi.Length / (1024.0 * 1024.0):F2} MB",
                        Date = fi.CreationTime.ToString("dd-MMM-yyyy HH:mm:ss")
                    })
                    .Select(b => $"{b.FileName} | {b.Size} | {b.Date}")
                    .ToList();

                return (true, $"Found {backupFiles.Count} backup files", backupFiles);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get backup history: {ex.Message}");
                return (false, $"Error: {ex.Message}", new List<string>());
            }
        }

        public async Task DeleteOldBackupsAsync(int keepDays = 30)
        {
            try
            {
                if (!Directory.Exists(_backupFolder))
                    return;

                var cutoffDate = DateTime.Now.AddDays(-keepDays);
                var oldBackups = Directory.GetFiles(_backupFolder, "AMAAN_PMS_Backup_*.bak")
                    .Where(f => new FileInfo(f).CreationTime < cutoffDate);

                foreach (var file in oldBackups)
                {
                    File.Delete(file);
                    _logger.LogInformation($"Deleted old backup: {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to delete old backups: {ex.Message}");
            }
        }
    }
}
