using AmaanParkingSystem.Services;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Controllers
{
    public class EmailConfigController : BaseController
    {
        private readonly EmailReportService _emailService;
        private readonly ILogger<EmailConfigController> _logger;

        // ✅ NEW: inject RealTimeReportScheduledService so we can reuse
        //         its Excel generation + email logic for manual sends
        private readonly RealTimeReportScheduledService _realTimeService;

        public EmailConfigController(
            EmailReportService emailService,
            ILogger<EmailConfigController> logger,
            RealTimeReportScheduledService realTimeService)   // ✅ ADD parameter
        {
            _emailService = emailService;
            _logger = logger;
            _realTimeService = realTimeService;               // ✅ ADD assignment
        }

        public IActionResult Index() => View();

        // ==========================
        // SMTP Configuration
        // ==========================
        [HttpGet]
        public async Task<IActionResult> GetSmtpConfig()
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                var config = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    @"SELECT TOP 1 id, config_name, smtp_server, smtp_port, smtp_username,
                             smtp_password, from_email, from_name, enable_ssl, is_active
                      FROM [AMAAN_PMS].[dbo].[db_tbl_22_email_config]
                      WHERE is_active = 1
                      ORDER BY id DESC");

                if (config == null)
                    return Json(new { success = false, message = "No email configuration found" });

                return Json(new { success = true, data = config });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateSmtpConfig([FromBody] SmtpConfigModel model)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                if (model.Id > 0)
                {
                    await conn.ExecuteAsync(
                        @"UPDATE [AMAAN_PMS].[dbo].[db_tbl_22_email_config]
                          SET smtp_server   = @SmtpServer,
                              smtp_port     = @SmtpPort,
                              smtp_username = @SmtpUsername,
                              smtp_password = @SmtpPassword,
                              from_email    = @FromEmail,
                              from_name     = @FromName,
                              enable_ssl    = @EnableSsl,
                              updated_on    = GETDATE()
                          WHERE id = @Id", model);
                }
                else
                {
                    await conn.ExecuteAsync(
                        @"INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_22_email_config]
                          (config_name, smtp_server, smtp_port, smtp_username, smtp_password,
                           from_email, from_name, enable_ssl, is_active, created_on)
                          VALUES ('Default', @SmtpServer, @SmtpPort, @SmtpUsername, @SmtpPassword,
                                  @FromEmail, @FromName, @EnableSsl, 1, GETDATE())", model);
                }
                return Json(new { success = true, message = "SMTP configuration saved successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> TestEmailConnection([FromBody] TestEmailRequest request)
        {
            try
            {
                var success = await _emailService.TestEmailConnectionAsync(request.TestEmail);
                return Json(new
                {
                    success,
                    message = success ? $"Test email sent to {request.TestEmail}" : "Failed to send test email"
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ==========================
        // Site Masters & Preferences
        // ==========================
        [HttpGet]
        public async Task<IActionResult> GetSiteMasters()
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());

                var sites = await conn.QueryAsync<dynamic>(@"
                    SELECT
                        site_id,
                        customer_id,
                        site_name,
                        client_name,
                        client_email,
                        client_site_address,
                        site_type,
                        ISNULL(receive_daily_exited,         0) AS receive_daily_exited,
                        ISNULL(receive_daily_transactions,   0) AS receive_daily_transactions,
                        ISNULL(receive_monthly_exited,       0) AS receive_monthly_exited,
                        ISNULL(receive_monthly_transactions, 0) AS receive_monthly_transactions,
                        ISNULL(receive_foc_report,           0) AS receive_foc_report,
                        ISNULL(receive_low_balance,          0) AS receive_low_balance,
                        ISNULL(receive_realtime_report,      0) AS receive_realtime_report    -- ✅ NEW
                    FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                    WHERE site_id IS NOT NULL
                    ORDER BY
                        CASE WHEN UPPER(site_type) = 'ADMIN' THEN 0 ELSE 1 END,
                        display_priority,
                        site_name");

                return Json(new { success = true, data = sites });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }
        // ==========================
        // Email Send Count / Stats
        // ==========================
        [HttpGet]
        public async Task<IActionResult> GetEmailStats()
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());

                var stats = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    @"SELECT
                        COUNT(*)                                         AS TotalSent,
                        SUM(CASE WHEN Status = 'Sent'   THEN 1 ELSE 0 END) AS SuccessCount,
                        SUM(CASE WHEN Status = 'Failed' THEN 1 ELSE 0 END) AS FailedCount,
                        SUM(CASE WHEN CAST(CreatedOn AS DATE) = CAST(GETDATE() AS DATE)
                                 THEN 1 ELSE 0 END)                      AS TodayCount,
                        SUM(CASE WHEN CreatedOn >= DATEADD(DAY,-6,CAST(GETDATE() AS DATE))
                                 THEN 1 ELSE 0 END)                      AS Last7DaysCount,
                        SUM(CASE WHEN CreatedOn >= DATEADD(DAY,-29,CAST(GETDATE() AS DATE))
                                 THEN 1 ELSE 0 END)                      AS Last30DaysCount,
                        SUM(CASE WHEN MONTH(CreatedOn) = MONTH(GETDATE())
                                  AND YEAR(CreatedOn)  = YEAR(GETDATE())
                                 THEN 1 ELSE 0 END)                      AS ThisMonthCount
                      FROM [AMAAN_PMS].[dbo].[db_tbl_29_email_logs]");

                return Json(new { success = true, data = stats });
            }
            catch (Exception ex)
            {
                _logger.LogError($"GetEmailStats error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateSitePreferences([FromBody] SitePreferenceModel model)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.ExecuteAsync(
                    @"UPDATE [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                      SET receive_daily_exited         = @ReceiveDailyExited,
                          receive_daily_transactions   = @ReceiveDailyTransactions,
                          receive_monthly_exited       = @ReceiveMonthlyExited,
                          receive_monthly_transactions = @ReceiveMonthlyTransactions,
                          receive_foc_report           = @ReceiveFocReport,
                          receive_low_balance          = @ReceiveLowBalance,
                          receive_realtime_report      = @ReceiveRealtimeReport,    -- ✅ NEW
                          updated_on = GETDATE()
                      WHERE site_id = @SiteId", model);

                return Json(new { success = true, message = "Preferences updated" });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ==========================
        // Send Custom Reports  (existing — now also validates RealTime)
        // ==========================
        [HttpPost]
        public async Task<IActionResult> SendCustomReports()
        {
            try
            {
                string requestBody;
                using (var reader = new StreamReader(Request.Body))
                    requestBody = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(requestBody))
                    return Json(new { success = false, message = "Request body is empty" });

                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var request = System.Text.Json.JsonSerializer.Deserialize<CustomReportRequest>(requestBody, options);

                if (request == null)
                    return Json(new { success = false, message = "Invalid JSON format" });

                if (request.SiteNames == null || request.SiteNames.Count == 0)
                    return Json(new { success = false, message = "Select at least one site" });

                if (!request.IncludeDailyExited && !request.IncludeDailyTransactions &&
                    !request.IncludeMonthlyExited && !request.IncludeMonthlyTransactions &&
                    !request.IncludeFocReport && !request.IncludeLowBalance)
                {
                    return Json(new { success = false, message = "Select at least one report" });
                }

                using var conn = new SqlConnection(GetDynamicConnectionString());
                var siteIds = new List<int>();

                foreach (var siteName in request.SiteNames)
                {
                    var siteId = await conn.QueryFirstOrDefaultAsync<int?>(
                        @"SELECT site_id FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                          WHERE LTRIM(RTRIM(site_name)) = LTRIM(RTRIM(@SiteName))
                          COLLATE SQL_Latin1_General_CP1_CI_AS",
                        new { SiteName = siteName });

                    if (siteId.HasValue) siteIds.Add(siteId.Value);
                }

                if (siteIds.Count == 0)
                    return Json(new { success = false, message = "No valid sites found." });

                var reportDate = DateTime.Now.AddDays(-1);

                var success = await _emailService.SendCustomReportsAsync(
                    siteIds,
                    request.IncludeDailyExited,
                    request.IncludeDailyTransactions,
                    request.IncludeMonthlyExited,
                    request.IncludeMonthlyTransactions,
                    reportDate,
                    request.IncludeFocReport,
                    request.IncludeLowBalance);

                return Json(new
                {
                    success,
                    message = success ? $"Reports sent to {siteIds.Count} site(s)" : "Failed to send reports. Check logs."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"ERROR: {ex.Message}\n{ex.StackTrace}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ==========================
        // ✅ NEW: Send Real-Time Reports manually from UI
        // ==========================
        [HttpPost]
        public async Task<IActionResult> SendRealTimeReports()
        {
            try
            {
                string requestBody;
                using (var reader = new StreamReader(Request.Body))
                    requestBody = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(requestBody))
                    return Json(new { success = false, message = "Request body is empty" });

                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var request = System.Text.Json.JsonSerializer.Deserialize<RealTimeReportRequest>(requestBody, options);

                if (request == null || request.SiteNames == null || request.SiteNames.Count == 0)
                    return Json(new { success = false, message = "Select at least one site" });

                // Resolve site names → recipients
                using var conn = new SqlConnection(GetDynamicConnectionString());
                var recipients = new List<(int siteId, string siteName, string clientName, string email)>();

                foreach (var siteName in request.SiteNames)
                {
                    var site = await conn.QueryFirstOrDefaultAsync<dynamic>(
                        @"SELECT site_id, site_name, client_name, client_email
                  FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                  WHERE LTRIM(RTRIM(site_name)) = LTRIM(RTRIM(@SiteName))
                    AND client_email IS NOT NULL
                    AND client_email != ''
                  COLLATE SQL_Latin1_General_CP1_CI_AS",
                        new { SiteName = siteName });

                    if (site != null)
                    {
                        // ✅ FIX: Use Convert.ToInt32 and .ToString() — never direct (int)/(string) cast on Dapper dynamic
                        int siteId = Convert.ToInt32(site.site_id);
                        string sName = site.site_name?.ToString() ?? siteName;
                        string clientName = site.client_name?.ToString() ?? "Valued Client";
                        string email = site.client_email?.ToString() ?? "";

                        if (!string.IsNullOrWhiteSpace(email))
                            recipients.Add((siteId, sName, clientName, email));
                    }
                }

                if (recipients.Count == 0)
                    return Json(new { success = false, message = "No valid sites with email found." });

                // Delegate to RealTimeReportScheduledService (reuses Excel + SMTP logic)
                var (sent, failed) = await _realTimeService.SendRealTimeReportManualAsync(recipients);

                return Json(new
                {
                    success = sent > 0,
                    message = $"Real-Time reports sent: {sent} ✅   Failed: {failed} ❌",
                    sent,
                    failed
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"SendRealTimeReports ERROR: {ex.Message}\n{ex.StackTrace}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ===========================
        // Models
        // ===========================
        public class SmtpConfigModel
        {
            public int Id { get; set; }
            public string SmtpServer { get; set; }
            public int SmtpPort { get; set; }
            public string SmtpUsername { get; set; }
            public string SmtpPassword { get; set; }
            public string FromEmail { get; set; }
            public string FromName { get; set; }
            public bool EnableSsl { get; set; }
        }

        public class SitePreferenceModel
        {
            public int SiteId { get; set; }
            public bool ReceiveDailyExited { get; set; }
            public bool ReceiveDailyTransactions { get; set; }
            public bool ReceiveMonthlyExited { get; set; }
            public bool ReceiveMonthlyTransactions { get; set; }
            public bool ReceiveFocReport { get; set; }
            public bool ReceiveLowBalance { get; set; }
            public bool ReceiveRealtimeReport { get; set; }   // ✅ NEW
        }

        public class TestEmailRequest
        {
            public string TestEmail { get; set; }
        }

        public class CustomReportRequest
        {
            public List<string> SiteNames { get; set; } = new();
            public bool IncludeDailyExited { get; set; }
            public bool IncludeDailyTransactions { get; set; }
            public bool IncludeMonthlyExited { get; set; }
            public bool IncludeMonthlyTransactions { get; set; }
            public bool IncludeFocReport { get; set; }
            public bool IncludeLowBalance { get; set; }
        }

        public class RealTimeReportRequest           // ✅ NEW model
        {
            public List<string> SiteNames { get; set; } = new();
        }
    }
}