using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Services
{
    public class ScheduledBackupService : BackgroundService
    {
        private readonly ILogger<ScheduledBackupService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private Timer _timer;

        public ScheduledBackupService(ILogger<ScheduledBackupService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Scheduled Backup Service is starting.");

            // ✅ PRODUCTION MODE: Calculate time until next 1:00 AM
            var now = DateTime.Now;
            var nextRun = DateTime.Today.AddDays(1).AddHours(1); // Next 1:00 AM

            if (now.Hour < 1)
            {
                nextRun = DateTime.Today.AddHours(1); // Today at 1:00 AM if before 1 AM
            }

            var timeUntilFirstRun = nextRun - now;

            _timer = new Timer(
                DoWork,
                null,
                timeUntilFirstRun,
                TimeSpan.FromHours(24)); // Run every 24 hours

            _logger.LogInformation($"Next backup scheduled at: {nextRun:yyyy-MM-dd HH:mm:ss}");

            return Task.CompletedTask;
        }


        private async void DoWork(object state)
        {
            try
            {
                _logger.LogInformation("Starting scheduled database backup...");

                using (var scope = _serviceProvider.CreateScope())
                {
                    var backupService = scope.ServiceProvider.GetRequiredService<DatabaseBackupService>();

                    var result = await backupService.CreateBackupAsync();

                    if (result.success)
                    {
                        _logger.LogInformation($"Scheduled backup completed: {result.message}");

                        // Delete backups older than 30 days
                        await backupService.DeleteOldBackupsAsync(30);
                    }
                    else
                    {
                        _logger.LogError($"Scheduled backup failed: {result.message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in scheduled backup: {ex.Message}");
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Scheduled Backup Service is stopping.");
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _timer?.Dispose();
            base.Dispose();
        }
    }
}
