using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Services
{
    public class BalanceMonitorBackgroundService : BackgroundService
    {
        private readonly ILogger<BalanceMonitorBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6);

        public BalanceMonitorBackgroundService(
            ILogger<BalanceMonitorBackgroundService> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("========================================");
            _logger.LogInformation("Balance Monitor Background Service STARTED");
            _logger.LogInformation($"Check Interval: Every {_checkInterval.TotalHours} hours");
            _logger.LogInformation("========================================");

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var emailService = scope.ServiceProvider.GetRequiredService<EmailReportService>();

                        bool isEnabled = emailService.GetAutomaticNotificationsEnabled();

                        if (isEnabled)
                        {
                            _logger.LogInformation("========================================");
                            _logger.LogInformation($"⚙️ Automatic Balance Check Started: {DateTime.Now:dd-MMM-yyyy hh:mm tt}");
                            _logger.LogInformation("========================================");

                            await emailService.SendSubCompanyLowBalanceNotificationsAsync(500m);

                            _logger.LogInformation($"✓ Automatic check completed. Next check in {_checkInterval.TotalHours} hours.");
                        }
                        else
                        {
                            _logger.LogInformation($"⏸️ Automatic notifications DISABLED - Skipping check at {DateTime.Now:dd-MMM-yyyy hh:mm tt}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ Error in Balance Monitor: {ex.Message}\n{ex.StackTrace}");
                }

                await Task.Delay(_checkInterval, stoppingToken);
            }

            _logger.LogInformation("Balance Monitor Background Service STOPPED.");
        }
    }
}
