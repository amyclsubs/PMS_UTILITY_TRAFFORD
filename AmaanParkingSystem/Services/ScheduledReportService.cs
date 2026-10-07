using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using Dapper;
using Microsoft.Extensions.Configuration;

namespace AmaanParkingSystem.Services
{
    public class ScheduledReportService : BackgroundService
    {
        private readonly ILogger<ScheduledReportService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private Timer _timer;

        // 🧪 TRIAL MODE: Set to true for 2-minute testing, false for production (12:01 AM)
        private const bool TRIAL_MODE = false;

        private readonly IConnectionStringService _connStrService;
        private readonly IConfiguration _configuration;   // ✅ ADD THIS LINE

        public ScheduledReportService(
    ILogger<ScheduledReportService> logger,
    IServiceProvider serviceProvider,
    IConnectionStringService connStrService,
    IConfiguration configuration)               // ✅ ADD
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _connStrService = connStrService;
            _configuration = configuration;             // ✅ ADD
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (TRIAL_MODE)
            {
                _logger.LogInformation("🧪 ============================================");
                _logger.LogInformation("🧪 TRIAL MODE ACTIVATED - Running every 2 minutes");
                _logger.LogInformation("🧪 ============================================");

                // Run immediately, then every 2 minutes
                _timer = new Timer(
                    async (state) => await DoWorkAsync(state),
                    null,
                    TimeSpan.Zero,              // Start immediately
                    TimeSpan.FromMinutes(2)     // Repeat every 2 minutes
                );

                _logger.LogInformation($"⏰ First run: NOW");
                _logger.LogInformation($"⏰ Next runs: Every 2 minutes");
            }
            else
            {
                _logger.LogInformation("✅ Scheduled Report Service STARTED (Production Mode)");

                // Production: Schedule to run daily at 12:01 AM (00:01)
                var now = DateTime.Now;
                var scheduledTime = new DateTime(now.Year, now.Month, now.Day, 0, 1, 0); // 12:01 AM

                // If we've already passed 12:01 AM today, schedule for tomorrow
                if (now > scheduledTime)
                {
                    scheduledTime = scheduledTime.AddDays(1);
                }

                var timeToGo = scheduledTime - now;

                _timer = new Timer(
                    async (state) => await DoWorkAsync(state),
                    null,
                    timeToGo,
                    TimeSpan.FromDays(1) // Repeat every 24 hours
                );

                _logger.LogInformation($"⏰ Next scheduled report send: {scheduledTime:yyyy-MM-dd HH:mm:ss}");
                _logger.LogInformation($"⏰ Time until next run: {timeToGo.TotalHours:F2} hours");
            }

            return Task.CompletedTask;
        }

        private async Task DoWorkAsync(object state)
        {
            try
            {
                _logger.LogInformation("\n========================================");
                _logger.LogInformation($"🚀 SCHEDULED DAILY REPORT EXECUTION {(TRIAL_MODE ? "(TRIAL MODE)" : "")}");
                _logger.LogInformation($"📅 Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _logger.LogInformation("========================================");

                using var scope = _serviceProvider.CreateScope();
                var emailService = scope.ServiceProvider.GetRequiredService<EmailReportService>();

                // ✅ Get YESTERDAY's date for daily reports (or TODAY in trial mode for testing)
                var reportDate = TRIAL_MODE ? DateTime.Now : DateTime.Now.AddDays(-1);

                _logger.LogInformation($"📊 Generating reports for: {reportDate:yyyy-MM-dd}");

                // ✅ OVERRIDE: Use a very high threshold so ALL records are included in the
                //    scheduled daily report, regardless of actual balance.
                //    This does NOT affect BalanceMonitorBackgroundService or live alert logic.
                const decimal reportBalanceThreshold = 99999999m; // ← CHANGED: threshold override for report only

                // Get all sites with their email preferences
                var sites = await GetSitesWithEmailPreferencesAsync();

                if (!sites.Any())
                {
                    _logger.LogWarning("⚠️ No sites found with email preferences enabled");
                    return;
                }

                _logger.LogInformation($"📧 Found {sites.Count} site(s) with email preferences");

                int totalEmailsSent = 0;
                int totalFailed = 0;

                foreach (var site in sites)
                {
                    try
                    {
                        _logger.LogInformation($"\n📤 Processing: {site.SiteName} ({site.ClientEmail})");
                        _logger.LogInformation($"   - Daily Exited: {site.ReceiveDailyExited}");
                        _logger.LogInformation($"   - Daily Transactions: {site.ReceiveDailyTransactions}");
                        _logger.LogInformation($"   - Monthly Exited: {site.ReceiveMonthlyExited}");
                        _logger.LogInformation($"   - Monthly Transactions: {site.ReceiveMonthlyTransactions}");
                        _logger.LogInformation($"   - FOC Report:         {site.ReceiveFocReport}");
                        _logger.LogInformation($"   - Low Balance Report: {site.ReceiveLowBalance}");
                        _logger.LogInformation($"   - Balance Threshold (report override): {reportBalanceThreshold}"); // ← CHANGED: log the override

                        var siteIds = new System.Collections.Generic.List<int> { site.SiteId };

                        // ✅ Send reports based on individual site preferences
                        // ← CHANGED: pass reportBalanceThreshold so ALL records are included in the daily report
                        bool sent = await emailService.SendCustomReportsAsync(
                            siteIds,
                            site.ReceiveDailyExited,
                            site.ReceiveDailyTransactions,
                            site.ReceiveMonthlyExited,
                            site.ReceiveMonthlyTransactions,
                            reportDate,
                            site.ReceiveFocReport,
                            site.ReceiveLowBalance,
                            reportBalanceThreshold);   // ← CHANGED: threshold override injected here

                        if (sent)
                        {
                            totalEmailsSent++;
                            _logger.LogInformation($"   ✅ SUCCESS - Sent to {site.ClientName}");
                        }
                        else
                        {
                            totalFailed++;
                            _logger.LogWarning($"   ❌ FAILED - Could not send to {site.ClientName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        totalFailed++;
                        _logger.LogError($"   ❌ ERROR sending to {site.ClientEmail}: {ex.Message}");
                    }
                }

                _logger.LogInformation("\n========================================");
                _logger.LogInformation($"✅ COMPLETED: {totalEmailsSent} email(s) sent successfully");
                _logger.LogInformation($"❌ FAILED: {totalFailed} email(s) failed");

                if (TRIAL_MODE)
                {
                    _logger.LogInformation($"🧪 Next run: In 2 minutes");
                }
                else
                {
                    _logger.LogInformation($"⏰ Next run: {DateTime.Now.AddDays(1):yyyy-MM-dd} 00:01:00");
                }

                _logger.LogInformation("========================================\n");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ CRITICAL ERROR in scheduled report service: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Get all sites with at least one email preference enabled (value = 1)
        /// </summary>
        private async Task<System.Collections.Generic.List<SiteEmailPreference>> GetSitesWithEmailPreferencesAsync()
        {
            try
            {
                var cs = _connStrService.GetConnectionString()
                      ?? _configuration.GetConnectionString("DefaultConnection")
                      ?? throw new InvalidOperationException("No connection string found.");
                using var conn = new SqlConnection(cs);

                var query = @"
            SELECT 
                site_id                                      AS SiteId,
                site_name                                    AS SiteName,
                client_name                                  AS ClientName,
                client_email                                 AS ClientEmail,
                ISNULL(receive_daily_exited, 0)              AS ReceiveDailyExited,
                ISNULL(receive_daily_transactions, 0)        AS ReceiveDailyTransactions,
                ISNULL(receive_monthly_exited, 0)            AS ReceiveMonthlyExited,
                ISNULL(receive_monthly_transactions, 0)      AS ReceiveMonthlyTransactions,
                ISNULL(receive_foc_report, 0)                AS ReceiveFocReport,
                ISNULL(receive_low_balance, 0)               AS ReceiveLowBalance
            FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
            WHERE client_email IS NOT NULL
              AND client_email != ''
              AND (
                  receive_daily_exited         = 1 OR
                  receive_daily_transactions   = 1 OR
                  receive_monthly_exited       = 1 OR
                  receive_monthly_transactions = 1 OR
                  receive_foc_report           = 1 OR
                  receive_low_balance          = 1
              )
            ORDER BY display_priority, site_name";

                var result = await conn.QueryAsync<SiteEmailPreference>(query);
                return result.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error getting site preferences: {ex.Message}");
                return new System.Collections.Generic.List<SiteEmailPreference>();
            }
        }

        public override void Dispose()
        {
            _timer?.Dispose();
            _logger.LogInformation($"🛑 Scheduled Report Service STOPPED {(TRIAL_MODE ? "(TRIAL MODE)" : "")}");
            base.Dispose();
        }
    }

    /// <summary>
    /// Helper class for site email preferences
    /// Maps to db_tbl_03_site_masters columns
    /// </summary>
    public class SiteEmailPreference
    {
        public int SiteId { get; set; }
        public string SiteName { get; set; }
        public string ClientName { get; set; }
        public string ClientEmail { get; set; }
        public bool ReceiveDailyExited { get; set; }
        public bool ReceiveDailyTransactions { get; set; }
        public bool ReceiveMonthlyExited { get; set; }
        public bool ReceiveMonthlyTransactions { get; set; }
        public bool ReceiveFocReport { get; set; }      // ✅ NEW
        public bool ReceiveLowBalance { get; set; }     // ✅ NEW
    }
}
