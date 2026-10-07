using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;
using AmaanParkingSystem.Controllers;

namespace AmaanParkingSystem.Services
{
    public class EmailReportService
    {
        private readonly IConnectionStringService _connStrService;
        private readonly ILogger<EmailReportService> _logger;
        private readonly CollectionController _collectionController;
        // ✅ NEW: FocReportService injection
        private readonly FocReportService _focReportService;

        // In-memory cache to track last notification sent (to avoid spam)
        private static readonly Dictionary<int, DateTime> _lastNotificationCache = new Dictionary<int, DateTime>();
        private static readonly object _cacheLock = new object();

        // Control automatic notifications
        private static bool _automaticNotificationsEnabled = false;
        private static readonly object _enabledLock = new object();

        private readonly IConfiguration _configuration;

        public EmailReportService(
            IConnectionStringService connStrService,
            IConfiguration configuration,
            ILogger<EmailReportService> logger,
            CollectionController collectionController,
            FocReportService focReportService)   // ✅ NEW PARAMETER
        {
            _connStrService = connStrService;
            _configuration = configuration;
            _logger = logger;
            _collectionController = collectionController;
            _focReportService = focReportService;   // ✅ NEW ASSIGNMENT
        }

        private SqlConnection GetConnection()
        {
            var cs = _connStrService.GetConnectionString();
            if (string.IsNullOrEmpty(cs))
                throw new InvalidOperationException("Database connection string is not configured.");
            return new SqlConnection(cs);
        }

        // ==========================
        // ✅ Get SMTP Settings from Database
        // ==========================
        private async Task<(string server, int port, string username, string password, string fromEmail, string fromName, bool ssl)> GetSmtpSettingsAsync()
        {
            using var conn = GetConnection();

            var config = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT TOP 1 
                    [smtp_server],
                    [smtp_port],
                    [smtp_username],
                    [smtp_password],
                    [from_email],
                    [from_name],
                    [enable_ssl]
                  FROM [AMAAN_PMS].[dbo].[db_tbl_22_email_config]
                  WHERE [is_active] = 1
                  ORDER BY [id] DESC");

            if (config == null)
            {
                throw new Exception("No active email configuration found in database");
            }

            return (
                (string)config.smtp_server,
                (int)config.smtp_port,
                (string)config.smtp_username,
                (string)config.smtp_password,
                (string)config.from_email,
                (string)config.from_name,
                (bool)config.enable_ssl
            );
        }

        // ==========================
        // Test Email Connection
        // ==========================
        public async Task<bool> TestEmailConnectionAsync(string testEmail)
        {
            try
            {
                var (smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl) = await GetSmtpSettingsAsync();

                using var client = new SmtpClient(smtpServer, smtpPort)
                {
                    Credentials = new NetworkCredential(smtpUsername, smtpPassword),
                    EnableSsl = enableSsl
                };

                using var message = new MailMessage
                {
                    From = new MailAddress(fromEmail, fromName),
                    Subject = "Test Email - Amaan PMS",
                    Body = "Test successful! Your email configuration is working correctly.",
                    IsBodyHtml = false
                };
                message.To.Add(testEmail);

                await client.SendMailAsync(message);
                _logger.LogInformation($"✓ Test email sent to {testEmail}");
                await LogEmailAsync("Test", testEmail, null, "Test Email - Amaan PMS", null, null, "Sent", null);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"✗ Test failed: {ex.Message}");
                return false;
            }
        }

        // ==========================
        // ✅ Send Reports Using CollectionController Methods
        // ==========================

        /// <summary>
        /// Send Daily Exited Cars Report to selected sites
        /// </summary>
        public async Task<bool> SendDailyExitedCarsReportAsync(DateTime reportDate, List<int> siteIds)
        {
            try
            {
                _logger.LogInformation($"Generating Daily Exited Cars report for {reportDate:dd-MMM-yyyy}");

                var excelBytes = await _collectionController.GenerateDailyExitedCarsExcelBytes(reportDate, reportDate);

                var fileName = $"Daily_Exited_Cars_{reportDate:dd-MMM-yyyy}.xlsx";
                var subject = $"Daily Exited Cars Report - {reportDate:dd-MMM-yyyy}";
                var body = BuildEmailBody("Valued Client", reportDate, 1, "Daily Exited Cars Report");

                await SendEmailWithByteAttachmentAsync(siteIds, subject, body, excelBytes, fileName);

                _logger.LogInformation($"✓ Daily Exited Cars report sent successfully to {siteIds.Count} site(s)");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"✗ Error sending Daily Exited Cars report: {ex.Message}");
                return false;

            }
        }



        /// <summary>
        /// Send Daily Transactions Report to selected sites
        /// </summary>
        public async Task<bool> SendDailyTransactionsReportAsync(DateTime reportDate, List<int> siteIds)
        {
            try
            {
                _logger.LogInformation($"Generating Daily Transactions report for {reportDate:dd-MMM-yyyy}");

                var excelBytes = await _collectionController.GenerateDailyTransactionsExcelBytes(reportDate, reportDate);

                var fileName = $"Daily_Transactions_{reportDate:dd-MMM-yyyy}.xlsx";
                var subject = $"Daily Transactions Report - {reportDate:dd-MMM-yyyy}";
                var body = BuildEmailBody("Valued Client", reportDate, 1, "Daily Transactions Report");

                await SendEmailWithByteAttachmentAsync(siteIds, subject, body, excelBytes, fileName);

                _logger.LogInformation($"✓ Daily Transactions report sent successfully to {siteIds.Count} site(s)");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"✗ Error sending Daily Transactions report: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send Monthly Exited Cars Report to selected sites
        /// </summary>
        public async Task<bool> SendMonthlyExitedCarsReportAsync(int year, int month, List<int> siteIds)
        {
            try
            {
                _logger.LogInformation($"Generating Monthly Exited Cars report for {month}/{year}");

                var excelBytes = await _collectionController.GenerateMonthlyExitedCarsExcelBytes(year, month);

                var reportDate = new DateTime(year, month, 1);
                var fileName = $"Monthly_Exited_Cars_{year}_{month:D2}.xlsx";
                var subject = $"Monthly Exited Cars Report - {reportDate:MMMM yyyy}";
                var body = BuildEmailBody("Valued Client", reportDate, 1, $"Monthly Exited Cars Report for {reportDate:MMMM yyyy}");

                await SendEmailWithByteAttachmentAsync(siteIds, subject, body, excelBytes, fileName);

                _logger.LogInformation($"✓ Monthly Exited Cars report sent successfully to {siteIds.Count} site(s)");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"✗ Error sending Monthly Exited Cars report: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send Monthly Transactions Report to selected sites
        /// </summary>
        public async Task<bool> SendMonthlyTransactionsReportAsync(int year, int month, List<int> siteIds)
        {
            try
            {
                _logger.LogInformation($"Generating Monthly Transactions report for {month}/{year}");

                var excelBytes = await _collectionController.GenerateMonthlyTransactionsExcelBytes(year, month);

                var reportDate = new DateTime(year, month, 1);
                var fileName = $"Monthly_Transactions_{year}_{month:D2}.xlsx";
                var subject = $"Monthly Transactions Report - {reportDate:MMMM yyyy}";
                var body = BuildEmailBody("Valued Client", reportDate, 1, $"Monthly Transactions Report for {reportDate:MMMM yyyy}");

                await SendEmailWithByteAttachmentAsync(siteIds, subject, body, excelBytes, fileName);

                _logger.LogInformation($"✓ Monthly Transactions report sent successfully to {siteIds.Count} site(s)");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"✗ Error sending Monthly Transactions report: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Generate FOC Report Excel bytes for the given date (start of day → end of day).
        /// Mirrors the logic in FocReportController.ExportExcel exactly.
        /// </summary>
        private async Task<byte[]> GenerateFocReportExcel(DateTime date)
        {
            // Full day range — no payment-source filter (all sources)
            var startDate = date.Date;
            var endDate = date.Date.AddDays(1).AddSeconds(-1);

            var data = await _focReportService.GetFocReportData(startDate, endDate, null);

            using var workbook = new ClosedXML.Excel.XLWorkbook();
            var worksheet = workbook.Worksheets.Add("FOC Report");

            string[] headers =
            {
                "Trans ID", "Card Name", "Entry Time", "Type", "Source",
                "Val. Time", "Val. Charges", "Val. Paymode",
                "Reval. Time", "Reval. Charges", "Reason", "Remark"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;
            }

            int row = 2;
            foreach (var item in data)
            {
                worksheet.Cell(row, 1).SetValue(item.transaction_id?.ToString() ?? "");
                worksheet.Cell(row, 2).SetValue(item.cardname?.ToString() ?? "");
                worksheet.Cell(row, 3).SetValue(item.entry_time?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "--");
                worksheet.Cell(row, 4).SetValue(item.transaction_type?.ToString() ?? "");
                worksheet.Cell(row, 5).SetValue(item.payment_source?.ToString() ?? "");

                worksheet.Cell(row, 6).SetValue(item.validation_time?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "--");
                worksheet.Cell(row, 7).SetValue(Convert.ToDouble(item.validation_charges ?? 0));
                worksheet.Cell(row, 8).SetValue(item.validation_paymode?.ToString() ?? "");

                worksheet.Cell(row, 9).SetValue(item.revalidation_time?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "--");
                worksheet.Cell(row, 10).SetValue(Convert.ToDouble(item.revalidation_charges ?? 0));
                worksheet.Cell(row, 11).SetValue(item.reason?.ToString() ?? "");
                worksheet.Cell(row, 12).SetValue(item.remark?.ToString() ?? "");
                row++;
            }

            worksheet.Columns().AdjustToContents();

            using var stream = new System.IO.MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }

        /// <summary>
        /// Generate Low Balance Report Excel bytes.
        /// Mirrors ExportLowBalanceReport in LowBalanceAlertsController exactly.
        /// Default threshold: 500. Fetches ALL card types (no filter).
        /// </summary>
        private async Task<byte[]> GenerateLowBalanceExcel(decimal threshold = 500m)
        {
            using var conn = GetConnection();

            var query = @"
    SELECT 
        c.[id],
        c.[car_parkers_sub_co_id],
        c.[car_parkers_sub_co_name],
        c.[sub_co_car_parkers_contact_person],
        c.[sub_co_car_parkers_contact_number],
        c.[sub_co_car_parkers_email],
        ISNULL(c.[Balance_Amount], 0)   AS current_balance,
        c.[Neg_Balance_allowed],
        c.[Last_Notified_Date],
        c.[applicable_card_type]        AS applicable_card_type   -- ✅ use directly, no JOIN needed
    FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] c
    WHERE ISNULL(c.[Balance_Amount], 0) < @Threshold
      AND c.[car_parkers_status] = 1
    ORDER BY c.[Balance_Amount] ASC, c.[car_parkers_sub_co_name] ASC";

            var lowBalanceCompanies = (await conn.QueryAsync(query, new { Threshold = threshold })).ToList();

            using var workbook = new ClosedXML.Excel.XLWorkbook();
            var sheet = workbook.Worksheets.Add("Low Balance Report");

            string[] headers =
            {
                "ID", "Sub Company", "Contact Person", "Contact Number",
                "Email", "Balance (KES)", "Neg Balance Allowed", "Last Notified"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var hCell = sheet.Cell(1, i + 1);
                hCell.Value = headers[i];
                hCell.Style.Font.Bold = true;
                hCell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;
            }

            int rowIndex = 2;
            foreach (dynamic company in lowBalanceCompanies)
            {
                sheet.Cell(rowIndex, 1).Value = company.car_parkers_sub_co_id?.ToString() ?? "N/A";
                sheet.Cell(rowIndex, 2).Value = company.car_parkers_sub_co_name?.ToString() ?? "N/A";
                sheet.Cell(rowIndex, 3).Value = company.sub_co_car_parkers_contact_person?.ToString() ?? "N/A";
                sheet.Cell(rowIndex, 4).Value = company.sub_co_car_parkers_contact_number?.ToString() ?? "N/A";
                sheet.Cell(rowIndex, 5).Value = company.sub_co_car_parkers_email?.ToString() ?? "N/A";
                sheet.Cell(rowIndex, 6).Value = Convert.ToDouble(company.current_balance ?? 0m);

                bool negAllowed = false;
                try
                {
                    var raw = company.Neg_Balance_allowed;
                    if (raw != null)
                        negAllowed = (raw == 1 || raw == true || raw.ToString() == "1");
                }
                catch { }

                var negCell = sheet.Cell(rowIndex, 7);
                negCell.Value = negAllowed ? "Allowed" : "Not Allowed";
                negCell.Style.Font.Bold = true;
                negCell.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
                negCell.Style.Fill.BackgroundColor = negAllowed
                                                        ? ClosedXML.Excel.XLColor.Green
                                                        : ClosedXML.Excel.XLColor.Red;

                sheet.Cell(rowIndex, 8).Value = company.Last_Notified_Date != null
                    ? Convert.ToDateTime(company.Last_Notified_Date).ToString("dd-MMM-yyyy hh:mm:ss tt")
                    : "Never";

                rowIndex++;
            }

            sheet.Columns().AdjustToContents();

            using var ms = new System.IO.MemoryStream();
            workbook.SaveAs(ms);
            return ms.ToArray();
        }

        // ==========================
        // Send Custom Reports (Multiple Reports in One Email)
        // ==========================
        // ==========================
        // Send Custom Reports (Multiple Reports in One Email)
        // ==========================
        public async Task<bool> SendCustomReportsAsync(
            List<int> siteIds,
            bool includeDailyExited,
            bool includeDailyTransactions,
            bool includeMonthlyExited,
            bool includeMonthlyTransactions,
            DateTime reportDate,
            bool includeFocReport = false,
            bool includeLowBalance = false,
            decimal balanceThreshold = 9999999m)   // ← ADDED: 9th parameter, default 0m keeps all existing callers unaffected
        {
            try
            {
                _logger.LogInformation("========================================");
                _logger.LogInformation("SENDING CUSTOM REPORTS");
                _logger.LogInformation($"Date: {reportDate:yyyy-MM-dd}");
                _logger.LogInformation($"Sites: {siteIds.Count}");
                _logger.LogInformation("========================================");

                using var conn = GetConnection();
                var sitesQuery = $@"
            SELECT site_id, site_name, client_name, client_email
            FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
            WHERE site_id IN ({string.Join(",", siteIds)}) AND client_email IS NOT NULL";

                var sites = await conn.QueryAsync<dynamic>(sitesQuery);

                if (!sites.Any())
                {
                    _logger.LogWarning("No valid sites found");
                    return false;
                }

                var (smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl) = await GetSmtpSettingsAsync();

                byte[] focBytes = null;
                byte[] lowBalanceBytes = null;

                if (includeFocReport)
                {
                    _logger.LogInformation("Generating FOC Report Excel...");
                    focBytes = await GenerateFocReportExcel(reportDate);
                    _logger.LogInformation("✓ FOC Report Excel generated");
                }

                if (includeLowBalance)
                {
                    _logger.LogInformation($"Generating Low Balance Report Excel (threshold: {balanceThreshold})..."); // ← ADDED: log threshold
                    lowBalanceBytes = await GenerateLowBalanceExcel(balanceThreshold);   // ← CHANGED: pass threshold instead of using default
                    _logger.LogInformation("✓ Low Balance Report Excel generated");
                }

                int successCount = 0;
                int failureCount = 0;

                foreach (var site in sites)
                {
                    try
                    {
                        var attachments = new List<(byte[] bytes, string fileName)>();

                        if (includeDailyExited)
                        {
                            var bytes = await _collectionController.GenerateDailyExitedCarsExcelBytes(reportDate, reportDate);
                            attachments.Add((bytes, $"Daily_Exited_Cars_{reportDate:dd-MMM-yyyy}.xlsx"));
                        }

                        if (includeDailyTransactions)
                        {
                            var bytes = await _collectionController.GenerateDailyTransactionsExcelBytes(reportDate, reportDate);
                            attachments.Add((bytes, $"Daily_Transactions_{reportDate:dd-MMM-yyyy}.xlsx"));
                        }

                        if (includeMonthlyExited)
                        {
                            var bytes = await _collectionController.GenerateMonthlyExitedCarsExcelBytes(reportDate.Year, reportDate.Month);
                            attachments.Add((bytes, $"Monthly_Exited_Cars_{reportDate.Year}_{reportDate.Month:D2}.xlsx"));
                        }

                        if (includeMonthlyTransactions)
                        {
                            var bytes = await _collectionController.GenerateMonthlyTransactionsExcelBytes(reportDate.Year, reportDate.Month);
                            attachments.Add((bytes, $"Monthly_Transactions_{reportDate.Year}_{reportDate.Month:D2}.xlsx"));
                        }

                        if (includeFocReport && focBytes != null)
                        {
                            attachments.Add((focBytes, $"FOC_Report_{reportDate:dd-MMM-yyyy}.xlsx"));
                        }

                        if (includeLowBalance && lowBalanceBytes != null)
                        {
                            attachments.Add((lowBalanceBytes, $"Low_Balance_Report_{DateTime.Now:yyyy-MM-dd}.xlsx"));
                        }

                        if (!attachments.Any())
                        {
                            _logger.LogWarning($"No reports selected for {site.client_email}");
                            continue;
                        }

                        bool sent = await SendEmailWithMultipleByteAttachmentsAsync(
                            smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl,
                            (string)site.client_email,
                            $"Parking Reports - {reportDate:dd-MMM-yyyy}",
                            BuildEmailBody((string)site.client_name, reportDate, attachments.Count, "Custom Reports"),
                            attachments);

                        if (sent)
                        {
                            _logger.LogInformation($"✓ Sent to {site.client_name} ({site.client_email}) - {attachments.Count} file(s)");
                            successCount++;
                        }
                        else
                        {
                            failureCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"✗ Failed: {site.client_email} - {ex.Message}");
                        failureCount++;
                    }
                }

                _logger.LogInformation($"========================================");
                _logger.LogInformation($"RESULT: {successCount} success / {failureCount} failed");
                _logger.LogInformation($"========================================");

                return failureCount == 0;
            }
            catch (Exception ex)
            {
                _logger.LogError($"ERROR: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // ==========================
        // Send Hierarchy Report (For CompaniesController)
        // ==========================
        public async Task<bool> SendHierarchyReportWithAttachmentAsync(
            int companyId,
            List<string> recipients,
            string subject,
            string body,
            byte[] attachmentBytes,
            string attachmentFileName)
        {
            try
            {
                var (smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl) = await GetSmtpSettingsAsync();

                using var client = new SmtpClient(smtpServer, smtpPort)
                {
                    Credentials = new NetworkCredential(smtpUsername, smtpPassword),
                    EnableSsl = enableSsl
                };

                using var message = new MailMessage
                {
                    From = new MailAddress(fromEmail, fromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = false
                };

                foreach (var recipient in recipients)
                {
                    if (!string.IsNullOrWhiteSpace(recipient))
                    {
                        message.To.Add(recipient);
                    }
                }

                var attachment = new Attachment(new MemoryStream(attachmentBytes), attachmentFileName,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                message.Attachments.Add(attachment);

                await client.SendMailAsync(message);
                _logger.LogInformation($"✓ Hierarchy report sent to {recipients.Count} recipient(s)");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"✗ Hierarchy email send error: {ex.Message}");
                return false;
            }
        }

        // ==========================
        // LOW BALANCE NOTIFICATION CONTROL
        // ==========================

        /// <summary>
        /// Enable or disable automatic low balance notifications
        /// </summary>
        public void SetAutomaticNotificationsEnabled(bool enabled)
        {
            lock (_enabledLock)
            {
                _automaticNotificationsEnabled = enabled;
                _logger.LogInformation($"Automatic low balance notifications {(enabled ? "ENABLED" : "DISABLED")}");
            }
        }

        /// <summary>
        /// Check if automatic notifications are enabled
        /// </summary>
        public bool GetAutomaticNotificationsEnabled()
        {
            lock (_enabledLock)
            {
                return _automaticNotificationsEnabled;
            }
        }

        /// <summary>
        /// Get automatic notification status with settings
        /// </summary>
        public async Task<dynamic> GetAutomaticNotificationStatusAsync()
        {
            try
            {
                bool isEnabled = GetAutomaticNotificationsEnabled();

                using var conn = GetConnection();
                var lowBalanceCount = await conn.QueryFirstOrDefaultAsync<int>(@"
                    SELECT COUNT(*) 
                    FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
                    WHERE ISNULL(Balance_Amount, 0) < 500
                      AND car_parkers_status = '1'
                      AND sub_co_car_parkers_email IS NOT NULL");

                return new
                {
                    isEnabled = isEnabled,
                    status = isEnabled ? "Running" : "Stopped",
                    lowBalanceCount = lowBalanceCount,
                    checkInterval = "Every 6 hours",
                    lastCheck = DateTime.Now.ToString("dd-MMM-yyyy hh:mm tt")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting notification status: {ex.Message}");
                return new
                {
                    isEnabled = false,
                    status = "Error",
                    lowBalanceCount = 0,
                    checkInterval = "Every 6 hours",
                    lastCheck = "N/A"
                };
            }
        }

        // ==========================
        // LOW BALANCE NOTIFICATION FOR SUBCOMPANIES
        // ==========================

        /// <summary>
        /// Send low balance notification to a single specific sub-company
        /// </summary>
        public async Task<bool> SendLowBalanceNotificationToCompanyAsync(int subCompanyId, decimal lowBalanceThreshold = 500m)
        {
            try
            {
                _logger.LogInformation($"Sending low balance notification to sub-company ID: {subCompanyId}");

                using var conn = GetConnection();

                var subCompany = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT 
                sc.id,
                sc.car_parkers_company_id,
                sc.car_parkers_sub_co_id,
                sc.car_parkers_sub_co_name,
                sc.sub_co_car_parkers_email,
                sc.sub_co_car_parkers_contact_person,
                sc.sub_co_car_parkers_contact_number, 
                sc.card_parkers_company_password,
                ISNULL(sc.Balance_Amount, 0) AS current_balance,
                mc.car_parkers_company_name AS master_company_name
            FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] sc
            LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master] mc 
                ON sc.car_parkers_company_id = mc.car_parkers_company_id
            WHERE sc.id = @SubCompanyId
              AND sc.car_parkers_status = '1'",
                    new { SubCompanyId = subCompanyId });

                if (subCompany == null)
                {
                    _logger.LogWarning($"Sub-company ID {subCompanyId} not found or inactive");
                    return false;
                }

                string recipientEmail = (string)subCompany.sub_co_car_parkers_email;

                if (string.IsNullOrWhiteSpace(recipientEmail))
                {
                    _logger.LogWarning($"No email address found for sub-company ID {subCompanyId}");
                    return false;
                }

                string companyName = (string)subCompany.car_parkers_sub_co_name ?? "Valued Customer";
                decimal currentBalance = (decimal)subCompany.current_balance;
                string subCoId = (string)subCompany.car_parkers_sub_co_id ?? subCompanyId.ToString();

                // Extract the new fields from the query results
                string loginId = (string)subCompany.sub_co_car_parkers_contact_number;
                string password = (string)subCompany.card_parkers_company_password;

                string topUpLink = $"{_configuration["AppSettings:BaseUrl"]}";

                var (smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl) = await GetSmtpSettingsAsync();

                // Pass all 7 required arguments to fix CS7036
                string emailBody = BuildLowBalanceEmailBody(companyName, currentBalance, lowBalanceThreshold, topUpLink, subCoId, loginId, password);

                bool emailSent = await SendLowBalanceEmailAsync(
                    smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl,
                    recipientEmail,
                    $"⚠️ Low Balance Alert - {companyName}",
                    emailBody
                );

                if (emailSent)
                {
                    lock (_cacheLock)
                    {
                        _lastNotificationCache[subCompanyId] = DateTime.Now;
                    }

                    _logger.LogInformation($"✅ Low balance notification sent to {companyName} ({recipientEmail}) - Balance: KES {currentBalance:N2}");
                    return true;
                }
                else
                {
                    _logger.LogError($"❌ Failed to send notification to {companyName} ({recipientEmail})");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"ERROR sending notification to sub-company ID {subCompanyId}: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }
        /// <summary>
        /// Send low balance notification to a single company with optional custom subject, body, and attachment
        /// </summary>
        public async Task<bool> SendSingleCompanyLowBalanceNotificationAsync(
            int subCompanyId,
            decimal lowBalanceThreshold,
            string customSubject = null,
            string customBody = null,
            byte[] attachmentBytes = null,
            string fileName = null)
        {
            try
            {
                _logger.LogInformation($"Sending individual low balance notification to sub-company ID: {subCompanyId}");

                using var conn = GetConnection();
                var subCompany = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT 
                sc.id, sc.car_parkers_sub_co_id, sc.car_parkers_sub_co_name,
                sc.sub_co_car_parkers_email, sc.sub_co_car_parkers_contact_number, 
                sc.card_parkers_company_password, ISNULL(sc.Balance_Amount, 0) AS current_balance
            FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] sc
            WHERE sc.id = @SubCompanyId AND sc.car_parkers_status = '1'",
                    new { SubCompanyId = subCompanyId });

                if (subCompany == null || string.IsNullOrWhiteSpace((string)subCompany.sub_co_car_parkers_email))
                {
                    _logger.LogWarning($"Sub-company ID {subCompanyId} not found or has no email.");
                    return false;
                }

                string recipientEmail = (string)subCompany.sub_co_car_parkers_email;
                string companyName = (string)subCompany.car_parkers_sub_co_name ?? "Valued Customer";
                decimal currentBalance = (decimal)subCompany.current_balance;
                string subCoId = (string)subCompany.car_parkers_sub_co_id ?? subCompanyId.ToString();
                string loginId = (string)subCompany.sub_co_car_parkers_contact_number;
                string password = (string)subCompany.card_parkers_company_password;

                // Build final subject
                string finalSubject;
                if (!string.IsNullOrWhiteSpace(customSubject))
                {
                    finalSubject = $"⚠️ Low Balance Alert - {companyName} - {customSubject}";
                }
                else
                {
                    finalSubject = $"⚠️ Low Balance Alert - {companyName}";
                }

                // Build final body
                string finalBody;
                if (!string.IsNullOrWhiteSpace(customBody))
                {
                    // Use custom template with inserted message
                    finalBody = BuildLowBalanceEmailBodyWithCustomText(
                        companyName,
                        currentBalance,
                        lowBalanceThreshold,
                        _configuration["AppSettings:BaseUrl"],
                        subCoId,
                        loginId,
                        password,
                        customBody);
                }
                else
                {
                    // Use standard template
                    finalBody = BuildLowBalanceEmailBody(
                        companyName,
                        currentBalance,
                        lowBalanceThreshold,
                        _configuration["AppSettings:BaseUrl"],
                        subCoId,
                        loginId,
                        password);
                }

                var (smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl) = await GetSmtpSettingsAsync();

                bool sent = await SendLowBalanceEmailWithAttachmentAsync(
                    smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl,
                    recipientEmail, finalSubject, finalBody, attachmentBytes, fileName);

                if (sent)
                {
                    lock (_cacheLock)
                    {
                        _lastNotificationCache[subCompanyId] = DateTime.Now;
                    }
                }

                return sent;
            }
            catch (Exception ex)
            {
                _logger.LogError($"ERROR in individual notification: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check and send low balance notifications to subcompanies (bulk).
        /// Updated to support optional custom Subject, Body, and Attachment while keeping 
        /// previous automated functionality fully intact.
        /// </summary>
        public async Task<bool> SendSubCompanyLowBalanceNotificationsAsync(
            decimal lowBalanceThreshold = 500m,
            string customSubject = null,
            string customBody = null,
            byte[] attachmentBytes = null,
            string fileName = null)
        {
            try
            {
                using var conn = GetConnection();
                var subCompanies = (await conn.QueryAsync<dynamic>(@"
            SELECT id, car_parkers_sub_co_id, car_parkers_sub_co_name, sub_co_car_parkers_email, 
                   sub_co_car_parkers_contact_number, card_parkers_company_password, ISNULL(Balance_Amount, 0) AS current_balance 
            FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] 
            WHERE Balance_Amount < @Threshold AND car_parkers_status = '1' AND sub_co_car_parkers_email IS NOT NULL",
                    new { Threshold = lowBalanceThreshold })).ToList();

                if (!subCompanies.Any())
                    return true;

                var (smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl) = await GetSmtpSettingsAsync();

                foreach (var sub in subCompanies)
                {
                    int subId = (int)sub.id;

                    // Only apply 24-hour cache check if NOT using custom subject (automatic mode)
                    lock (_cacheLock)
                    {
                        if (string.IsNullOrEmpty(customSubject) && _lastNotificationCache.ContainsKey(subId))
                        {
                            if ((DateTime.Now - _lastNotificationCache[subId]).TotalHours < 24)
                                continue;
                        }
                    }

                    string companyName = (string)sub.car_parkers_sub_co_name;

                    // Build final subject
                    string finalSubject;
                    if (!string.IsNullOrWhiteSpace(customSubject))
                    {
                        finalSubject = $"⚠️ Low Balance Alert - {companyName} - {customSubject}";
                    }
                    else
                    {
                        finalSubject = $"⚠️ Low Balance Alert - {companyName}";
                    }

                    // Build final body
                    string finalBody;
                    if (!string.IsNullOrWhiteSpace(customBody))
                    {
                        // Use custom template with inserted message
                        finalBody = BuildLowBalanceEmailBodyWithCustomText(
                            companyName,
                            (decimal)sub.current_balance,
                            lowBalanceThreshold,
                            _configuration["AppSettings:BaseUrl"],
                            (string)sub.car_parkers_sub_co_id,
                            (string)sub.sub_co_car_parkers_contact_number,
                            (string)sub.card_parkers_company_password,
                            customBody);
                    }
                    else
                    {
                        // Use standard template
                        finalBody = BuildLowBalanceEmailBody(
                            companyName,
                            (decimal)sub.current_balance,
                            lowBalanceThreshold,
                            _configuration["AppSettings:BaseUrl"],
                            (string)sub.car_parkers_sub_co_id,
                            (string)sub.sub_co_car_parkers_contact_number,
                            (string)sub.card_parkers_company_password);
                    }

                    bool sent = await SendLowBalanceEmailWithAttachmentAsync(
                        smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl,
                        (string)sub.sub_co_car_parkers_email, finalSubject, finalBody, attachmentBytes, fileName);

                    if (sent)
                    {
                        lock (_cacheLock)
                            _lastNotificationCache[subId] = DateTime.Now;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                return false;
            }
        }

        private async Task<bool> SendLowBalanceEmailWithAttachmentAsync(string server, int port, string user, string pass, string from, string name, bool ssl, string to, string subject, string body, byte[] attachment, string fileName)
        {
            try
            {
                using var client = new SmtpClient(server, port) { Credentials = new NetworkCredential(user, pass), EnableSsl = ssl };
                using var msg = new MailMessage { From = new MailAddress(from, name), Subject = subject, Body = body, IsBodyHtml = true };
                msg.To.Add(to);
                if (attachment != null) msg.Attachments.Add(new Attachment(new MemoryStream(attachment), fileName ?? "attachment.xlsx"));
                await client.SendMailAsync(msg);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Send individual low balance email
        /// </summary>
        private async Task<bool> SendLowBalanceEmailAsync(
            string smtpServer, int smtpPort, string smtpUsername, string smtpPassword,
            string fromEmail, string fromName, bool enableSsl,
            string toEmail, string subject, string body)
        {
            try
            {
                using var client = new SmtpClient(smtpServer, smtpPort)
                {
                    Credentials = new NetworkCredential(smtpUsername, smtpPassword),
                    EnableSsl = enableSsl,
                    Timeout = 30000
                };

                using var message = new MailMessage
                {
                    From = new MailAddress(fromEmail, fromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = true,
                    Priority = MailPriority.High
                };
                message.To.Add(toEmail);

                await client.SendMailAsync(message);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Email send error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Build low balance notification email HTML
        /// </summary>
        private string BuildLowBalanceEmailBody(string companyName, decimal currentBalance, decimal threshold, string topUpLink, string subCoId, string loginId, string password)
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <style>
        body {{ font-family: Arial, sans-serif; background: #f4f4f4; padding: 20px; }}
        .container {{ max-width: 600px; margin: 0 auto; background: white; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }}
        .header {{ background: linear-gradient(135deg, #d9534f 0%, #c9302c 100%); color: white; padding: 30px; text-align: center; }}
        .header h1 {{ margin: 0; font-size: 28px; }}
        .content {{ padding: 30px; }}
        .alert-box {{ background: #fff3cd; border-left: 5px solid #ffc107; padding: 20px; margin: 20px 0; border-radius: 5px; }}
        .balance-info {{ font-size: 24px; font-weight: bold; color: #d9534f; margin: 10px 0; }}
        .threshold-info {{ font-size: 16px; color: #5cb85c; margin: 10px 0; }}
        .btn {{ display: inline-block; padding: 15px 35px; background: linear-gradient(135deg, #5cb85c 0%, #4cae4c 100%); color: white; text-decoration: none; border-radius: 5px; margin: 25px 0; font-size: 18px; font-weight: bold; box-shadow: 0 4px 6px rgba(0,0,0,0.1); }}
        .btn:hover {{ background: linear-gradient(135deg, #4cae4c 0%, #449d44 100%); }}
        .details {{ background: #f9f9f9; padding: 20px; border-radius: 5px; margin: 20px 0; }}
        .details-item {{ margin: 10px 0; font-size: 14px; }}
        .details-label {{ font-weight: bold; color: #333; }}
        .footer {{ text-align: center; padding: 20px; color: #777; font-size: 12px; border-top: 1px solid #ddd; }}
        .warning-icon {{ font-size: 48px; margin-bottom: 10px; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <div class='warning-icon'>⚠️</div>
            <h1>Low Balance Alert</h1>
        </div>
        <div class='content'>
            <h2>Dear {companyName},</h2>
            
            <p>This is an automated notification from <strong>AMAAN Parking Management System</strong>.</p>
            
            <div class='alert-box'>
                <p style='margin: 0 0 10px 0;'><strong>⚠️ Your account balance is running low!</strong></p>
                <div class='balance-info'>Current Balance: KES {currentBalance:N2}</div>
                <div class='threshold-info'>Minimum Required: KES {threshold:N2}</div>
            </div>
            
            <p style='font-size: 16px; line-height: 1.6;'>
                To ensure <strong>uninterrupted parking services</strong>, please top up your account as soon as possible.
            </p>
            
            <div style='text-align: center;'>
                <a href='{topUpLink}' class='btn'>🔄 TOP UP NOW</a>
            </div>
            
            <div class='details'>
                <p style='margin: 0 0 15px 0; font-weight: bold; color: #2c3e50;'>Account Details:</p>
                <div class='details-item'>
                    <span class='details-label'>Sub-Company ID:</span> {subCoId}
                </div>
                <div class='details-item'>
                    <span class='details-label'>Company Name:</span> {companyName}
                </div>
                <div class='details-item'>
                    <span class='details-label'>Current Balance:</span> KES {currentBalance:N2}
                </div>
                
                <hr style='border: 0; border-top: 1px solid #ddd; margin: 15px 0;' />
                <p style='margin: 0 0 10px 0; font-weight: bold; color: #d9534f;'>Login Credentials:</p>
                <div class='details-item'>
                    <span class='details-label'>Login ID:</span> {loginId}
                </div>
                <div class='details-item'>
                    <span class='details-label'>Password:</span> {password}
                </div>
                <hr style='border: 0; border-top: 1px solid #ddd; margin: 15px 0;' />

                <div class='details-item'>
                    <span class='details-label'>Minimum Balance:</span> KES {threshold:N2}
                </div>
                <div class='details-item'>
                    <span class='details-label'>Notification Time:</span> {DateTime.Now:dd-MMM-yyyy hh:mm tt}
                </div>
            </div>
            
            <p style='font-size: 14px; color: #666; margin-top: 20px;'>
                If you have already topped up, please disregard this message. For any assistance, please contact our support team.
            </p>
            
            <p style='margin-top: 30px;'>
                Best regards,<br>
                <strong>AMAAN Parking Management System</strong>
            </p>
        </div>
        <div class='footer'>
            <p>This is an automated message. Please do not reply to this email.</p>
            <p>© {DateTime.Now.Year} AMAAN Parking Management System. All rights reserved.</p>
        </div>
    </div>
</body>
</html>";
        }

        /// <summary>
        /// Build low balance notification email HTML with custom message inserted
        /// </summary>
        private string BuildLowBalanceEmailBodyWithCustomText(
            string companyName,
            decimal currentBalance,
            decimal threshold,
            string topUpLink,
            string subCoId,
            string loginId,
            string password,
            string customMessage)
        {
            // Convert line breaks to HTML breaks
            string formattedCustomMessage = customMessage.Replace("\n", "<br/>").Replace("\r", "");

            return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <style>
        body {{ font-family: Arial, sans-serif; background: #f4f4f4; padding: 20px; }}
        .container {{ max-width: 600px; margin: 0 auto; background: white; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }}
        .header {{ background: linear-gradient(135deg, #d9534f 0%, #c9302c 100%); color: white; padding: 30px; text-align: center; }}
        .header h1 {{ margin: 0; font-size: 28px; }}
        .content {{ padding: 30px; }}
        .alert-box {{ background: #fff3cd; border-left: 5px solid #ffc107; padding: 20px; margin: 20px 0; border-radius: 5px; }}
        .balance-info {{ font-size: 24px; font-weight: bold; color: #d9534f; margin: 10px 0; }}
        .threshold-info {{ font-size: 16px; color: #5cb85c; margin: 10px 0; }}
        .btn {{ display: inline-block; padding: 15px 35px; background: linear-gradient(135deg, #5cb85c 0%, #4cae4c 100%); color: white; text-decoration: none; border-radius: 5px; margin: 25px 0; font-size: 18px; font-weight: bold; box-shadow: 0 4px 6px rgba(0,0,0,0.1); }}
        .btn:hover {{ background: linear-gradient(135deg, #4cae4c 0%, #449d44 100%); }}
        .details {{ background: #f9f9f9; padding: 20px; border-radius: 5px; margin: 20px 0; }}
        .details-item {{ margin: 10px 0; font-size: 14px; }}
        .details-label {{ font-weight: bold; color: #333; }}
        .footer {{ text-align: center; padding: 20px; color: #777; font-size: 12px; border-top: 1px solid #ddd; }}
        .warning-icon {{ font-size: 48px; margin-bottom: 10px; }}
        .custom-message {{ background: #e8f4fd; border-left: 5px solid #2196F3; padding: 15px; margin: 20px 0; border-radius: 5px; font-size: 15px; line-height: 1.6; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <div class='warning-icon'>⚠️</div>
            <h1>Low Balance Alert</h1>
        </div>
        <div class='content'>
            <h2>Dear {companyName},</h2>
            
            <p>This is an automated notification from <strong>AMAAN Parking Management System</strong>.</p>
            
            <div class='custom-message'>
                {formattedCustomMessage}
            </div>
            
            <div class='alert-box'>
                <p style='margin: 0 0 10px 0;'><strong>⚠️ Your account balance is running low!</strong></p>
                <div class='balance-info'>Current Balance: KES {currentBalance:N2}</div>
                <div class='threshold-info'>Minimum Required: KES {threshold:N2}</div>
            </div>
            
            <p style='font-size: 16px; line-height: 1.6;'>
                To ensure <strong>uninterrupted parking services</strong>, please top up your account as soon as possible.
            </p>
            
            <div style='text-align: center;'>
                <a href='{topUpLink}' class='btn'>🔄 TOP UP NOW</a>
            </div>
            
            <div class='details'>
                <p style='margin: 0 0 15px 0; font-weight: bold; color: #2c3e50;'>Account Details:</p>
                <div class='details-item'>
                    <span class='details-label'>Sub-Company ID:</span> {subCoId}
                </div>
                <div class='details-item'>
                    <span class='details-label'>Company Name:</span> {companyName}
                </div>
                <div class='details-item'>
                    <span class='details-label'>Current Balance:</span> KES {currentBalance:N2}
                </div>
                
                <hr style='border: 0; border-top: 1px solid #ddd; margin: 15px 0;' />
                <p style='margin: 0 0 10px 0; font-weight: bold; color: #d9534f;'>Login Credentials:</p>
                <div class='details-item'>
                    <span class='details-label'>Login ID:</span> {loginId}
                </div>
                <div class='details-item'>
                    <span class='details-label'>Password:</span> {password}
                </div>
                <hr style='border: 0; border-top: 1px solid #ddd; margin: 15px 0;' />
                
                <div class='details-item'>
                    <span class='details-label'>Minimum Balance:</span> KES {threshold:N2}
                </div>
                <div class='details-item'>
                    <span class='details-label'>Notification Time:</span> {DateTime.Now:dd-MMM-yyyy hh:mm tt}
                </div>
            </div>
            
            <p style='font-size: 14px; color: #666; margin-top: 20px;'>
                If you have already topped up, please disregard this message. For any assistance, please contact our support team.
            </p>
            
            <p style='margin-top: 30px;'>
                Best regards,<br/>
                <strong>AMAAN Parking Management System</strong>
            </p>
        </div>
        <div class='footer'>
            <p>This is an automated message. Please do not reply to this email.</p>
            <p>© {DateTime.Now.Year} AMAAN Parking Management System. All rights reserved.</p>
        </div>
    </div>
</body>
</html>";
        }

        /// <summary>
        /// Get balance summary for dashboard
        /// </summary>
        public async Task<dynamic> GetSubCompanyBalanceSummaryAsync(decimal lowBalanceThreshold = 500m)
        {
            try
            {
                using var conn = GetConnection();

                var summary = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT 
                        COUNT(*) AS TotalSubCompanies,
                        COUNT(CASE WHEN ISNULL(Balance_Amount, 0) < @LowBalanceThreshold THEN 1 END) AS LowBalanceCount,
                        COUNT(CASE WHEN ISNULL(Balance_Amount, 0) >= @LowBalanceThreshold THEN 1 END) AS HealthyBalanceCount,
                        ISNULL(SUM(Balance_Amount), 0) AS TotalBalance,
                        ISNULL(AVG(Balance_Amount), 0) AS AverageBalance,
                        ISNULL(MIN(Balance_Amount), 0) AS MinimumBalance,
                        ISNULL(MAX(Balance_Amount), 0) AS MaximumBalance
                    FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
                    WHERE car_parkers_status = '1'",
                    new { LowBalanceThreshold = lowBalanceThreshold });

                return summary;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting balance summary: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get list of low balance companies - Database values only (no cache)
        /// </summary>
        public async Task<List<dynamic>> GetLowBalanceSubCompaniesWithCacheAsync(decimal lowBalanceThreshold = 500m, string cardType = "All")
        {
            try
            {
                using var conn = GetConnection();

                var sql = @"
            SELECT
                c.[id] AS id,
                c.[car_parkers_company_id] AS carparkerscompanyid,
                c.[car_parkers_sub_co_id] AS carparkerssubcoid,
                c.[car_parkers_sub_co_name] AS carparkerssubconame,
                c.[sub_co_car_parkers_email] AS subcocarparkersemail,
                c.[sub_co_car_parkers_contact_person] AS subcocarparkerscontactperson,
                c.[sub_co_car_parkers_contact_number] AS subcocarparkerscontactnumber,
                c.[applicable_card_type] AS applicablecardtype,
                c.[Last_Notified_Date] AS lastNotifiedDate,
                CAST(ISNULL(c.[Neg_Balance_allowed], 0) AS bit) AS negBalanceallowed,
                ISNULL(c.[Balance_Amount], 0) AS currentbalance,
                mc.[car_parkers_company_name] AS mastercompanyname
            FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] c
            LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master] mc
                ON c.[car_parkers_company_id] = mc.[car_parkers_company_id]
            WHERE ISNULL(c.[Balance_Amount], 0) < @LowBalanceThreshold
              AND c.[car_parkers_status] = 1
              AND (@CardType = 'All' OR ISNULL(c.[applicable_card_type], '') = @CardType)
            ORDER BY c.[Balance_Amount] ASC, c.[car_parkers_sub_co_name] ASC";

                var rows = await conn.QueryAsync<dynamic>(sql, new
                {
                    LowBalanceThreshold = lowBalanceThreshold,
                    CardType = cardType ?? "All"
                });

                var result = new List<dynamic>();

                foreach (var c in rows)
                {
                    bool negAllowed = false;
                    try
                    {
                        var raw = c.negBalanceallowed;
                        if (raw != null)
                        {
                            var s = raw.ToString().Trim();
                            negAllowed = s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch { }

                    object lastNotified = null;
                    try
                    {
                        lastNotified = c.lastNotifiedDate;
                    }
                    catch { }

                    result.Add(new
                    {
                        id = c.id,
                        carparkerscompanyid = c.carparkerscompanyid,
                        carparkerssubcoid = c.carparkerssubcoid,
                        carparkerssubconame = c.carparkerssubconame,
                        subcocarparkersemail = c.subcocarparkersemail,
                        subcocarparkerscontactperson = c.subcocarparkerscontactperson,
                        subcocarparkerscontactnumber = c.subcocarparkerscontactnumber,
                        applicablecardtype = c.applicablecardtype,
                        currentbalance = c.currentbalance,
                        mastercompanyname = c.mastercompanyname,
                        lastNotifiedDate = lastNotified,
                        negBalanceallowed = negAllowed
                    });
                }

                _logger.LogInformation("Returned {Count} low balance companies.", result.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting low balance companies");
                throw;
            }
        }

        // ==========================
        // CORE EMAIL SENDING METHODS (Using byte[])
        // ==========================

        /// <summary>
        /// Send email with single byte[] attachment to multiple sites
        /// </summary>
        private async Task SendEmailWithByteAttachmentAsync(
            List<int> siteIds,
            string subject,
            string body,
            byte[] attachmentBytes,
            string fileName)
        {
            var sites = await GetSiteDetailsAsync(siteIds);
            var (smtpServer, smtpPort, smtpUsername, smtpPassword, fromEmail, fromName, enableSsl) = await GetSmtpSettingsAsync();

            foreach (var site in sites.Where(s => !string.IsNullOrWhiteSpace(s.client_email)))
            {
                try
                {
                    using var client = new SmtpClient(smtpServer, smtpPort)
                    {
                        Credentials = new NetworkCredential(smtpUsername, smtpPassword),
                        EnableSsl = enableSsl,
                        Timeout = 30000
                    };

                    using var message = new MailMessage
                    {
                        From = new MailAddress(fromEmail, fromName),
                        Subject = subject,
                        Body = body.Replace("Valued Client", site.client_name ?? "Valued Client"),
                        IsBodyHtml = true
                    };
                    message.To.Add(site.client_email);

                    var attachment = new Attachment(
                        new MemoryStream(attachmentBytes),
                        fileName,
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                    message.Attachments.Add(attachment);

                    await client.SendMailAsync(message);
                    _logger.LogInformation($"  ✓ Email sent to {site.client_email} ({site.site_name})");
                    await LogEmailAsync("Report", (string)site.client_email, null, subject,
                        (int?)site.site_id, (string)site.site_name, "Sent", null);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"  ✗ Failed to send to {site.client_email}: {ex.Message}");
                    await LogEmailAsync("Report", (string)site.client_email, null, subject,
                        (int?)site.site_id, (string)site.site_name, "Failed", ex.Message);
                }
            }
        }

        /// <summary>
        /// Send email with multiple byte[] attachments
        /// </summary>
        private async Task<bool> SendEmailWithMultipleByteAttachmentsAsync(
            string smtpServer, int smtpPort, string smtpUsername, string smtpPassword,
            string fromEmail, string fromName, bool enableSsl,
            string toEmail, string subject, string body,
            List<(byte[] bytes, string fileName)> attachments)
        {
            try
            {
                using var client = new SmtpClient(smtpServer, smtpPort)
                {
                    Credentials = new NetworkCredential(smtpUsername, smtpPassword),
                    EnableSsl = enableSsl,
                    Timeout = 30000
                };

                using var message = new MailMessage
                {
                    From = new MailAddress(fromEmail, fromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = true
                };
                message.To.Add(toEmail);

                foreach (var (bytes, fileName) in attachments)
                {
                    var attachment = new Attachment(
                        new MemoryStream(bytes),
                        fileName,
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                    message.Attachments.Add(attachment);
                }

                await client.SendMailAsync(message);
                await LogEmailAsync("Report", toEmail, null, subject, null, null, "Sent", null);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Email send error: {ex.Message}");
                await LogEmailAsync("Report", toEmail, null, subject, null, null, "Failed", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Get site details from database
        /// </summary>
        private async Task<List<dynamic>> GetSiteDetailsAsync(List<int> siteIds)
        {
            try
            {
                using var conn = GetConnection();

                var query = @"
                    SELECT 
                        site_id,
                        site_name,
                        client_name,
                        client_email,
                        client_site_address
                    FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                    WHERE site_id IN @SiteIds";

                var sites = await conn.QueryAsync<dynamic>(query, new { SiteIds = siteIds });
                return sites.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"ERROR getting site details: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Build professional HTML email body
        /// </summary>
        private string BuildEmailBody(string clientName, DateTime date, int attachmentCount, string reportType)
        {
            return $@"
<!DOCTYPE html>
<html>
<body style='font-family: Arial, sans-serif; background: #f4f4f4; padding: 20px;'>
    <div style='max-width: 900px; margin: 0 auto; background: white; border-radius: 8px; padding: 30px; box-shadow: 0 2px 4px rgba(0,0,0,0.1);'>
        <div style='text-align: center; margin-bottom: 30px;'>
            <h2 style='color: #2c3e50; margin: 0; border-bottom: 3px solid #3498db; padding-bottom: 15px;'>
                Amaan Parking Management System
            </h2>
        </div>
        
        <div style='margin: 20px 0;'>
            <p style='font-size: 16px;'>Dear <strong>{clientName}</strong>,</p>
            
            <p style='font-size: 14px; line-height: 1.6;'>
                Please find attached <strong>{attachmentCount}</strong> detailed parking report(s) for 
                <strong>{date:dd-MMMM-yyyy}</strong>.
            </p>
            
            <div style='background: #ecf0f1; padding: 15px; border-left: 4px solid #3498db; margin: 20px 0;'>
                <p style='margin: 0; font-size: 14px;'>
                    <strong>Report Type:</strong> {reportType}<br>
                    <strong>Date:</strong> {date:dd-MMMM-yyyy}<br>
                    <strong>Attachments:</strong> {attachmentCount} Excel file(s)
                </p>
            </div>
            
            <p style='font-size: 14px; line-height: 1.6;'>
                If you have any questions or need additional information, please don't hesitate to contact us.
            </p>
        </div>
        
        <div style='margin-top: 40px; padding-top: 20px; border-top: 1px solid #ddd; text-align: center;'>
            <p style='color: #7f8c8d; font-size: 12px; margin: 5px 0;'>
                © {DateTime.Now.Year} Amaan Parking Management System. All rights reserved.
            </p>
            <p style='color: #95a5a6; font-size: 11px; margin: 5px 0;'>
                This is an automated message. Please do not reply to this email.
            </p>
        </div>
    </div>
</body>
</html>";
        }
        /// <summary>
        /// Insert one row into db_tbl_29_email_logs.
        /// All parameters are optional except emailType, recipientEmail, and status.
        /// </summary>
        private async Task LogEmailAsync(
            string emailType,
            string recipientEmail,
            string ccEmail,
            string subject,
            int? companyId,
            string companyName,
            string status,            // "Sent" | "Failed"
            string errorMessage)
        {
            try
            {
                using var conn = GetConnection();
                await conn.ExecuteAsync(
                    @"INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_29_email_logs]
                        (EmailType, RecipientEmail, CCEmail, Subject,
                         CompanyId, CompanyName, SiteId, SiteName,
                         Status, ErrorMessage, CreatedOn)
                      VALUES
                        (@EmailType, @RecipientEmail, @CCEmail, @Subject,
                         @CompanyId, @CompanyName, NULL, @CompanyName,
                         @Status, @ErrorMessage, GETDATE())",
                    new
                    {
                        EmailType = emailType ?? "General",
                        RecipientEmail = recipientEmail,
                        CCEmail = ccEmail,
                        Subject = subject,
                        CompanyId = companyId,
                        CompanyName = companyName,
                        Status = status,
                        ErrorMessage = errorMessage
                    });
            }
            catch (Exception ex)
            {
                // Never let logging break the actual send flow
                _logger.LogWarning($"LogEmailAsync failed (non-fatal): {ex.Message}");
            }
        }
    }

}
