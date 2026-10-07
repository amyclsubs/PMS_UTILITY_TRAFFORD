using AmaanParkingSystem.Controllers;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Services
{
    public class RealTimeReportScheduledService : BackgroundService
    {
        private readonly ILogger<RealTimeReportScheduledService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionStringService _connStrService;
        private readonly IConfiguration _configuration;
        private Timer _timer;

        private const bool TRIAL_MODE = false;

        public RealTimeReportScheduledService(
            ILogger<RealTimeReportScheduledService> logger,
            IServiceProvider serviceProvider,
            IConnectionStringService connStrService,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _connStrService = connStrService;
            _configuration = configuration;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (TRIAL_MODE)
            {
                _logger.LogInformation("🧪 REAL-TIME REPORT SERVICE — TRIAL MODE (every 2 mins)");
                _timer = new Timer(async (state) => await DoWorkAsync(state), null, TimeSpan.Zero, TimeSpan.FromMinutes(2));
            }
            else
            {
                _logger.LogInformation("✅ Real-Time Report Scheduled Service STARTED (Production Mode — 3:00 AM)");
                var now = DateTime.Now;
                var scheduledTime = new DateTime(now.Year, now.Month, now.Day, 3, 0, 0);
                if (now > scheduledTime) scheduledTime = scheduledTime.AddDays(1);
                var timeToGo = scheduledTime - now;
                _timer = new Timer(async (state) => await DoWorkAsync(state), null, timeToGo, TimeSpan.FromDays(1));
                _logger.LogInformation($"⏰ Next Real-Time report send: {scheduledTime:yyyy-MM-dd HH:mm:ss}");
                _logger.LogInformation($"⏰ Time until next run: {timeToGo.TotalHours:F2} hours");
            }
            return Task.CompletedTask;
        }

        private async Task DoWorkAsync(object state)
        {
            try
            {
                _logger.LogInformation("========================================");
                _logger.LogInformation($"🚀 REAL-TIME REPORT EXECUTION {(TRIAL_MODE ? "(TRIAL MODE)" : "")}");
                _logger.LogInformation($"📅 Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _logger.LogInformation("========================================");

                // ✅ SAME logic as RealTimeReportController — two separate queries
                var currentlyParked = await GetCurrentlyParkedAsync();
                var overdueRows = await GetOverdueRowsAsync();

                _logger.LogInformation($"   📋 Currently Parked (today):  {currentlyParked.Count}");
                _logger.LogInformation($"   ⚠️  Overdue (entered before today & still inside): {overdueRows.Count}");

                if (!currentlyParked.Any() && !overdueRows.Any())
                {
                    _logger.LogWarning("⚠️ No parked cars found. No emails will be sent.");
                    return;
                }

                var currentlyParkedExcel = BuildCurrentlyParkedExcel(currentlyParked);
                var overdueExcel = BuildOverdueExcel(overdueRows);

                var sites = await GetSitesForRealTimeReportAsync();
                if (!sites.Any())
                {
                    _logger.LogWarning("⚠️ No sites with email configured for real-time reports.");
                    return;
                }

                _logger.LogInformation($"📧 Sending to {sites.Count} site(s)");

                var smtpSettings = await GetSmtpSettingsAsync();
                int sent = 0, failed = 0;
                var reportDate = DateTime.Now;

                foreach (var site in sites)
                {
                    try
                    {
                        _logger.LogInformation($"📤 Sending to: {site.SiteName} ({site.ClientEmail})");

                        var attachments = new List<(byte[] bytes, string fileName)>
                        {
                            (currentlyParkedExcel, $"CurrentlyParked_{reportDate:yyyy-MM-dd_HH-mm}.xlsx"),
                            (overdueExcel,          $"OverdueCars_{reportDate:yyyy-MM-dd_HH-mm}.xlsx")
                        };

                        string subject = $"Real-Time Parking Report — {reportDate:dd-MMM-yyyy HH:mm}";
                        string body = BuildEmailBody(site.ClientName ?? "Valued Client", reportDate, currentlyParked.Count, overdueRows.Count);

                        bool ok = await SendEmailWithMultipleAttachmentsAsync(smtpSettings, site.ClientEmail, subject, body, attachments);

                        if (ok) { sent++; _logger.LogInformation($"   ✅ SUCCESS — {site.ClientName}"); }
                        else { failed++; _logger.LogWarning($"   ❌ FAILED  — {site.ClientName}"); }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogError($"   ❌ ERROR sending to {site.ClientEmail}: {ex.Message}");
                    }
                }

                _logger.LogInformation("========================================");
                _logger.LogInformation($"✅ Real-Time Reports sent: {sent}  ❌ Failed: {failed}");
                _logger.LogInformation(TRIAL_MODE ? "🧪 Next run: In 2 minutes" : $"⏰ Next run: {DateTime.Now.AddDays(1):yyyy-MM-dd} 03:00:00");
                _logger.LogInformation("========================================\n");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ CRITICAL ERROR in RealTimeReportScheduledService: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Called by EmailConfigController for manual sends from UI.
        /// </summary>
        public async Task<(int sent, int failed)> SendRealTimeReportManualAsync(
            IEnumerable<(int siteId, string siteName, string clientName, string email)> recipients)
        {
            // ✅ SAME logic as RealTimeReportController — two separate queries
            var currentlyParked = await GetCurrentlyParkedAsync();
            var overdueRows = await GetOverdueRowsAsync();

            _logger.LogInformation($"[Manual RT Report] Currently Parked: {currentlyParked.Count}, Overdue: {overdueRows.Count}");

            var currentlyParkedExcel = BuildCurrentlyParkedExcel(currentlyParked);
            var overdueExcel = BuildOverdueExcel(overdueRows);

            var smtpSettings = await GetSmtpSettingsAsync();
            int sent = 0, failed = 0;
            var reportDate = DateTime.Now;

            foreach (var (_, siteName, clientName, email) in recipients)
            {
                try
                {
                    var attachments = new List<(byte[] bytes, string fileName)>
                    {
                        (currentlyParkedExcel, $"CurrentlyParked_{reportDate:yyyy-MM-dd_HH-mm}.xlsx"),
                        (overdueExcel,          $"OverdueCars_{reportDate:yyyy-MM-dd_HH-mm}.xlsx")
                    };

                    string subject = $"Real-Time Parking Report (Manual) — {reportDate:dd-MMM-yyyy HH:mm}";
                    string body = BuildEmailBody(clientName, reportDate, currentlyParked.Count, overdueRows.Count);

                    bool ok = await SendEmailWithMultipleAttachmentsAsync(smtpSettings, email, subject, body, attachments);

                    if (ok) { sent++; _logger.LogInformation($"[Manual RT] ✅ Sent to {siteName} ({email})"); }
                    else { failed++; _logger.LogWarning($"[Manual RT] ❌ Failed for {siteName} ({email})"); }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError($"[Manual RT] ❌ Exception for {email}: {ex.Message}");
                }
            }

            return (sent, failed);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // DATA FETCHING — EXACT same SQL as RealTimeReportController
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Currently Parked = entered TODAY and still inside.
        /// ✅ EXACT same WHERE as RealTimeReportController.GetCurrentEntries()
        /// </summary>
        private async Task<List<RealTimeEntryRow>> GetCurrentlyParkedAsync()
        {
            try
            {
                var cs = _connStrService.GetConnectionString()
                      ?? _configuration.GetConnectionString("DefaultConnection")
                      ?? throw new InvalidOperationException("No connection string found.");

                using var conn = new SqlConnection(cs);

                const string sql = @"
                    SELECT
                          [id], [customer_id], [cardno], [card_name], [card_type],
                          [car_plate_no], [entry_time], [fee_schedule_name], [action_status],
                          [charges], [charge_status], [remark], [balance_to_collect], [pay_mode],
                          [paid_amount], [transaction_status], [isValidated], [current_charges],
                          [real_time_charges], [car_parkers_company_id], [car_parkers_sub_co_id],
                          [car_parkers_sub_co_name], [mpesa_receipt_number],
                          [park_time_untill_validation], [validation_time]
                    FROM dbo.[db_tbl_14_car_enterd]
                    WHERE CAST([entry_time] AS DATE) = CAST(GETDATE() AS DATE)
                      AND [action_status] = 'Entered'
                    ORDER BY [entry_time] DESC;";

                var rows = (await conn.QueryAsync<RealTimeEntryRow>(sql)).ToList();
                EnrichRows(rows);
                _logger.LogInformation($"GetCurrentlyParkedAsync: {rows.Count} rows");
                return rows;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error in GetCurrentlyParkedAsync: {ex.Message}");
                return new List<RealTimeEntryRow>();
            }
        }

        /// <summary>
        /// Overdue = entered BEFORE TODAY and still inside (action_status = Entered).
        /// ✅ EXACT same WHERE as RealTimeReportController.GetPastEntries()
        /// A car that entered on 26-Apr 11 PM and is still inside after midnight → appears here.
        /// </summary>
        private async Task<List<RealTimeEntryRow>> GetOverdueRowsAsync()
        {
            try
            {
                var cs = _connStrService.GetConnectionString()
                      ?? _configuration.GetConnectionString("DefaultConnection")
                      ?? throw new InvalidOperationException("No connection string found.");

                using var conn = new SqlConnection(cs);

                const string sql = @"
                    SELECT
                          [id], [customer_id], [cardno], [card_name], [card_type],
                          [car_plate_no], [entry_time], [fee_schedule_name], [action_status],
                          [charges], [charge_status], [remark], [balance_to_collect], [pay_mode],
                          [paid_amount], [transaction_status], [isValidated], [current_charges],
                          [real_time_charges], [car_parkers_company_id], [car_parkers_sub_co_id],
                          [car_parkers_sub_co_name], [mpesa_receipt_number],
                          [park_time_untill_validation], [validation_time]
                    FROM dbo.[db_tbl_14_car_enterd]
                    WHERE CAST([entry_time] AS DATE) < CAST(GETDATE() AS DATE)
                      AND [action_status] = 'Entered'
                    ORDER BY [entry_time] ASC;";

                var rows = (await conn.QueryAsync<RealTimeEntryRow>(sql)).ToList();
                EnrichRows(rows);
                _logger.LogInformation($"GetOverdueRowsAsync: {rows.Count} rows");
                return rows;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error in GetOverdueRowsAsync: {ex.Message}");
                return new List<RealTimeEntryRow>();
            }
        }

        private void EnrichRows(List<RealTimeEntryRow> rows)
        {
            foreach (var row in rows)
            {
                if (row.entry_time.HasValue)
                {
                    row.entry_date = row.entry_time.Value.ToString("dd-MMM-yyyy");
                    row.entry_time_only = row.entry_time.Value.ToString("HH:mm:ss");
                    row.park_time_live = FormatDuration(DateTime.Now - row.entry_time.Value);
                }
                row.display_charges = row.real_time_charges ?? row.current_charges ?? row.charges ?? 0;
            }
        }

        private async Task<List<RealTimeSiteInfo>> GetSitesForRealTimeReportAsync()
        {
            try
            {
                var cs = _connStrService.GetConnectionString()
                      ?? _configuration.GetConnectionString("DefaultConnection")
                      ?? throw new InvalidOperationException("No connection string found.");

                using var conn = new SqlConnection(cs);

                const string query = @"
                    SELECT
                        site_id      AS SiteId,
                        site_name    AS SiteName,
                        client_name  AS ClientName,
                        client_email AS ClientEmail
                    FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                    WHERE receive_realtime_report = 1
                      AND client_email IS NOT NULL
                      AND client_email != ''
                    ORDER BY display_priority, site_name";

                var result = await conn.QueryAsync<RealTimeSiteInfo>(query);
                return result.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error getting sites for real-time report: {ex.Message}");
                return new List<RealTimeSiteInfo>();
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // EXCEL GENERATION
        // ═══════════════════════════════════════════════════════════════════════

        private byte[] BuildCurrentlyParkedExcel(List<RealTimeEntryRow> rows)
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.Worksheets.Add("Currently Parked");

            string[] headers = {
                "Card No", "Card Name", "Card Type", "Plate No",
                "Entry Date", "Entry Time", "Duration Parked",
                "Sub Company", "Fee Schedule", "Current Charges (KES)",
                "Charge Status", "Transaction Status", "Remark"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = sheet.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2C3E50");
                cell.Style.Font.FontColor = XLColor.White;
            }

            int rowIdx = 2;
            foreach (var r in rows)
            {
                sheet.Cell(rowIdx, 1).Value = r.cardno ?? "";
                sheet.Cell(rowIdx, 2).Value = r.card_name ?? "";
                sheet.Cell(rowIdx, 3).Value = r.card_type ?? "";
                sheet.Cell(rowIdx, 4).Value = r.car_plate_no ?? "";
                sheet.Cell(rowIdx, 5).Value = r.entry_date ?? "";
                sheet.Cell(rowIdx, 6).Value = r.entry_time_only ?? "";
                sheet.Cell(rowIdx, 7).Value = r.park_time_live ?? "";
                sheet.Cell(rowIdx, 8).Value = r.car_parkers_sub_co_name ?? "";
                sheet.Cell(rowIdx, 9).Value = r.fee_schedule_name ?? "";
                sheet.Cell(rowIdx, 10).Value = Convert.ToDouble(r.display_charges);
                sheet.Cell(rowIdx, 11).Value = r.charge_status ?? "";
                sheet.Cell(rowIdx, 12).Value = r.transaction_status ?? "";
                sheet.Cell(rowIdx, 13).Value = r.remark ?? "";
                rowIdx++;
            }

            sheet.Cell(rowIdx + 1, 1).Value = $"Total Records: {rows.Count}";
            sheet.Cell(rowIdx + 1, 1).Style.Font.Bold = true;
            sheet.Cell(rowIdx + 1, 1).Style.Font.FontColor = XLColor.FromHtml("#2980B9");
            sheet.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            return ms.ToArray();
        }

        private byte[] BuildOverdueExcel(List<RealTimeEntryRow> rows)
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.Worksheets.Add("Overdue Cars");

            string[] headers = {
                "Card No", "Card Name", "Card Type", "Plate No",
                "Entry Date", "Entry Time", "Duration Parked (OVERDUE)",
                "Sub Company", "Fee Schedule", "Current Charges (KES)",
                "Charge Status", "Transaction Status", "Remark"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = sheet.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#C0392B");
                cell.Style.Font.FontColor = XLColor.White;
            }

            int rowIdx = 2;
            foreach (var r in rows)
            {
                sheet.Cell(rowIdx, 1).Value = r.cardno ?? "";
                sheet.Cell(rowIdx, 2).Value = r.card_name ?? "";
                sheet.Cell(rowIdx, 3).Value = r.card_type ?? "";
                sheet.Cell(rowIdx, 4).Value = r.car_plate_no ?? "";
                sheet.Cell(rowIdx, 5).Value = r.entry_date ?? "";
                sheet.Cell(rowIdx, 6).Value = r.entry_time_only ?? "";

                var durationCell = sheet.Cell(rowIdx, 7);
                durationCell.Value = r.park_time_live ?? "";
                durationCell.Style.Font.FontColor = XLColor.Red;
                durationCell.Style.Font.Bold = true;

                sheet.Cell(rowIdx, 8).Value = r.car_parkers_sub_co_name ?? "";
                sheet.Cell(rowIdx, 9).Value = r.fee_schedule_name ?? "";
                sheet.Cell(rowIdx, 10).Value = Convert.ToDouble(r.display_charges);
                sheet.Cell(rowIdx, 11).Value = r.charge_status ?? "";
                sheet.Cell(rowIdx, 12).Value = r.transaction_status ?? "";
                sheet.Cell(rowIdx, 13).Value = r.remark ?? "";

                sheet.Row(rowIdx).Style.Fill.BackgroundColor = XLColor.FromHtml("#FADBD8");
                rowIdx++;
            }

            sheet.Cell(rowIdx + 1, 1).Value = $"Total Overdue: {rows.Count}  (entered before today, still parked)";
            sheet.Cell(rowIdx + 1, 1).Style.Font.Bold = true;
            sheet.Cell(rowIdx + 1, 1).Style.Font.FontColor = XLColor.Red;
            sheet.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            return ms.ToArray();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // SMTP
        // ═══════════════════════════════════════════════════════════════════════

        private async Task<(string server, int port, string username, string password,
                             string fromEmail, string fromName, bool ssl)> GetSmtpSettingsAsync()
        {
            var cs = _connStrService.GetConnectionString()
                  ?? _configuration.GetConnectionString("DefaultConnection")
                  ?? throw new InvalidOperationException("No connection string found.");

            using var conn = new SqlConnection(cs);

            var config = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT TOP 1 smtp_server, smtp_port, smtp_username, smtp_password,
                               from_email, from_name, enable_ssl
                  FROM [AMAAN_PMS].[dbo].[db_tbl_22_email_config]
                  WHERE is_active = 1
                  ORDER BY id DESC");

            if (config == null)
                throw new Exception("No active email configuration found in database.");

            return (Convert.ToString(config.smtp_server),
                    Convert.ToInt32(config.smtp_port),
                    Convert.ToString(config.smtp_username),
                    Convert.ToString(config.smtp_password),
                    Convert.ToString(config.from_email),
                    Convert.ToString(config.from_name),
                    Convert.ToBoolean(config.enable_ssl));
        }

        private async Task<bool> SendEmailWithMultipleAttachmentsAsync(
            (string server, int port, string username, string password,
             string fromEmail, string fromName, bool ssl) smtp,
            string toEmail, string subject, string body,
            List<(byte[] bytes, string fileName)> attachments)
        {
            try
            {
                using var client = new SmtpClient(smtp.server, smtp.port)
                {
                    Credentials = new NetworkCredential(smtp.username, smtp.password),
                    EnableSsl = smtp.ssl,
                    Timeout = 30000
                };

                using var message = new MailMessage
                {
                    From = new MailAddress(smtp.fromEmail, smtp.fromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = true
                };

                message.To.Add(toEmail);

                foreach (var (bytes, fileName) in attachments)
                    message.Attachments.Add(new Attachment(
                        new MemoryStream(bytes), fileName,
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

                await client.SendMailAsync(message);
                await LogToEmailTableAsync(toEmail, subject, "RealTime-Report", "Sent", null);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Email send error: {ex.Message}");
                await LogToEmailTableAsync(toEmail, subject, "RealTime-Report", "Failed", ex.Message);
                return false;
            }
        }
        private async Task LogToEmailTableAsync(string recipientEmail, string subject, string emailType, string status, string errorMessage)
        {
            try
            {
                var cs = _connStrService.GetConnectionString()
                      ?? _configuration.GetConnectionString("DefaultConnection");
                using var conn = new SqlConnection(cs);
                await conn.ExecuteAsync(
                    @"INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_29_email_logs]
                        (EmailType, RecipientEmail, CCEmail, Subject, CompanyId, CompanyName, SiteId, SiteName, Status, ErrorMessage, CreatedOn)
                      VALUES
                        (@EmailType, @RecipientEmail, NULL, @Subject, NULL, NULL, NULL, NULL, @Status, @ErrorMessage, GETDATE())",
                    new { EmailType = emailType, RecipientEmail = recipientEmail, Subject = subject, Status = status, ErrorMessage = errorMessage });
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"LogToEmailTableAsync failed (non-fatal): {ex.Message}");
            }
        }
        // ═══════════════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════════════

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalSeconds < 0) return "00:00:00";
            return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
        }

        private static string BuildEmailBody(string clientName, DateTime reportDate,
            int currentlyParkedCount, int overdueCount)
        {
            return $@"<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8"">
  <style>
    body {{ font-family: Arial, sans-serif; background: #f4f4f4; padding: 20px; }}
    .container {{ max-width: 620px; margin: 0 auto; background: #fff; border-radius: 8px; padding: 30px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); }}
    .header {{ text-align: center; border-bottom: 3px solid #2C3E50; padding-bottom: 15px; margin-bottom: 20px; }}
    .header h2 {{ color: #2C3E50; margin: 0; }}
    .stat-box {{ display: inline-block; width: 45%; text-align: center; padding: 15px; border-radius: 6px; margin: 5px; }}
    .blue {{ background: #EBF5FB; border: 1px solid #2980B9; }}
    .red  {{ background: #FADBD8; border: 1px solid #C0392B; }}
    .stat-number {{ font-size: 32px; font-weight: bold; }}
    .blue .stat-number {{ color: #2980B9; }}
    .red  .stat-number {{ color: #C0392B; }}
    .stat-label {{ font-size: 13px; color: #555; margin-top: 4px; }}
    .footer {{ text-align: center; font-size: 11px; color: #aaa; margin-top: 25px; }}
  </style>
</head>
<body>
  <div class=""container"">
    <div class=""header"">
      <h2>🅿️ Amaan Parking Management System</h2>
      <p style=""color:#555;margin:5px 0 0"">Real-Time Parking Report — {reportDate:dd-MMM-yyyy HH:mm}</p>
    </div>
    <p>Dear <strong>{clientName}</strong>,</p>
    <p>Please find attached the real-time parking status report generated at <strong>{reportDate:HH:mm:ss}</strong> on <strong>{reportDate:dd-MMM-yyyy}</strong>.</p>
    <div style=""text-align:center;margin:20px 0"">
      <div class=""stat-box blue"">
        <div class=""stat-number"">{currentlyParkedCount}</div>
        <div class=""stat-label"">Currently Parked<br>(entered today)</div>
      </div>
      <div class=""stat-box red"">
        <div class=""stat-number"">{overdueCount}</div>
        <div class=""stat-label"">Overdue Cars<br>(entered before today, still inside)</div>
      </div>
    </div>
    <p><strong>Attachments:</strong></p>
    <ul>
      <li>📋 <strong>CurrentlyParked_{reportDate:yyyy-MM-dd_HH-mm}.xlsx</strong> — Vehicles that entered today</li>
      <li>⚠️ <strong>OverdueCars_{reportDate:yyyy-MM-dd_HH-mm}.xlsx</strong> — Vehicles from previous days still parked</li>
    </ul>
    <p style=""font-size:13px;color:#777;border-top:1px solid #ddd;padding-top:15px;"">
      This report is automatically generated at 3:00 AM daily. It is separate from the daily collection/transaction reports sent at 12:01 AM.
    </p>
    <p style=""margin-top:20px"">Best regards,<br><strong>Amaan Parking Management System</strong></p>
    <div class=""footer"">
      <p>This is an automated message. Please do not reply.</p>
      <p>© {DateTime.Now.Year} Amaan Parking Management System. All rights reserved.</p>
    </div>
  </div>
</body>
</html>";
        }

        public override void Dispose()
        {
            _timer?.Dispose();
            _logger.LogInformation($"🛑 Real-Time Report Scheduled Service STOPPED {(TRIAL_MODE ? "(TRIAL MODE)" : "")}");
            base.Dispose();
        }
    }

    public class RealTimeSiteInfo
    {
        public int SiteId { get; set; }
        public string SiteName { get; set; }
        public string ClientName { get; set; }
        public string ClientEmail { get; set; }
    }
}
