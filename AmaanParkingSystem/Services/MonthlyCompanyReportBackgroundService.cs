using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmaanParkingSystem.Services
{
    /// <summary>
    /// Background service that runs on the 1st of every month.
    /// Sends the EXACT same Company Wise Report Excel (identical SQL queries, column headers,
    /// cell values, and formatting) as CompanyWiseReportController.GenerateCompanyWiseReportBytes,
    /// filtered by MONTH() and YEAR() of the previous calendar month — matching the controller's
    /// own month/year filter path exactly.
    /// </summary>
    public class MonthlyCompanyReportBackgroundService : BackgroundService
    {
        private readonly ILogger<MonthlyCompanyReportBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;

        // Duplicate-send guard: "YYYY-MM" keys for months already processed this session.
        private readonly HashSet<string> _sentMonths = new HashSet<string>();
        private readonly object _sentMonthsLock = new object();

        public MonthlyCompanyReportBackgroundService(
            ILogger<MonthlyCompanyReportBackgroundService> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("========================================");
            _logger.LogInformation("Monthly Company Report Service STARTED");
            _logger.LogInformation("Runs: 1st of every month");
            _logger.LogInformation("Report: Full previous calendar month");
            _logger.LogInformation("========================================");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;

                    if (now.Day == 1)
                    {
                        var reportMonth = now.AddMonths(-1);
                        int year = reportMonth.Year;
                        int month = reportMonth.Month;

                        string monthKey = $"{year}-{month:D2}";

                        bool alreadySent;
                        lock (_sentMonthsLock)
                        {
                            alreadySent = _sentMonths.Contains(monthKey);
                        }

                        if (!alreadySent)
                        {
                            _logger.LogInformation("========================================");
                            _logger.LogInformation($"MONTHLY REPORT TRIGGER: {now:yyyy-MM-dd HH:mm:ss}");
                            _logger.LogInformation($"Sending reports for: {new DateTime(year, month, 1):MMMM yyyy}");
                            _logger.LogInformation("========================================");

                            await SendMonthlyCompanyReportsAsync(year, month, stoppingToken);

                            lock (_sentMonthsLock)
                            {
                                _sentMonths.Add(monthKey);
                            }

                            _logger.LogInformation($"Monthly report batch for {monthKey} marked as SENT.");
                        }
                        else
                        {
                            _logger.LogInformation($"Monthly report for {monthKey} already sent this session. Skipping.");
                        }
                    }

                    // Wake every 30 minutes to check if it is the 1st of the month
                    await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"CRITICAL ERROR in MonthlyCompanyReportBackgroundService: {ex.Message}\n{ex.StackTrace}");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }

            _logger.LogInformation("Monthly Company Report Service STOPPED.");
        }

        private async Task SendMonthlyCompanyReportsAsync(int year, int month, CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var connStrService = scope.ServiceProvider.GetRequiredService<IConnectionStringService>();

            var connStr = connStrService.GetConnectionString();
            if (string.IsNullOrEmpty(connStr))
            {
                _logger.LogError("Connection string is not configured. Monthly report aborted.");
                return;
            }

            // Load SMTP from database — same query as EmailReportService.GetSmtpSettingsAsync
            dynamic smtpConfig;
            try
            {
                using var adminConn = new SqlConnection(connStr);
                smtpConfig = await adminConn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT TOP 1
                        [smtp_server], [smtp_port], [smtp_username], [smtp_password],
                        [from_email], [from_name], [enable_ssl]
                    FROM [AMAAN_PMS].[dbo].[db_tbl_22_email_config]
                    WHERE [is_active] = 1
                    ORDER BY [id] DESC");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load SMTP configuration: {ex.Message}");
                return;
            }

            if (smtpConfig == null)
            {
                _logger.LogError("No active SMTP configuration found. Monthly report aborted.");
                return;
            }

            // Load all active companies that have an email address
            List<dynamic> companies;
            try
            {
                using var conn = new SqlConnection(connStr);
                var result = await conn.QueryAsync<dynamic>(@"
                    SELECT
                        sc.[car_parkers_sub_co_id],
                        sc.[car_parkers_sub_co_name],
                        sc.[sub_co_car_parkers_email]
                    FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] sc
                    WHERE sc.[car_parkers_status] = '1'
                      AND sc.[sub_co_car_parkers_email] IS NOT NULL
                      AND LTRIM(RTRIM(sc.[sub_co_car_parkers_email])) != ''
                    ORDER BY sc.[car_parkers_sub_co_name]");

                companies = result.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load companies: {ex.Message}");
                return;
            }

            if (!companies.Any())
            {
                _logger.LogWarning("No active companies with email addresses found. Monthly report skipped.");
                return;
            }

            _logger.LogInformation($"Found {companies.Count} company/companies to email.");

            int successCount = 0;
            int failCount = 0;

            foreach (var company in companies)
            {
                if (stoppingToken.IsCancellationRequested) break;

                string companyName = (string)company.car_parkers_sub_co_name ?? "Valued Customer";
                string recipientEmail = (string)company.sub_co_car_parkers_email;

                try
                {
                    _logger.LogInformation($"Processing: {companyName} -> {recipientEmail}");

                    byte[] excelBytes = await GenerateCompanyWiseReportBytes(connStr, companyName, month, year);

                    if (excelBytes == null || excelBytes.Length == 0)
                    {
                        _logger.LogWarning($"  No report data generated for {companyName}. Skipping.");
                        failCount++;
                        continue;
                    }

                    bool sent = await SendEmailWithAttachment(smtpConfig, recipientEmail, companyName, excelBytes, month, year, connStr);

                    if (sent)
                    {
                        _logger.LogInformation($"  SUCCESS - Sent to {companyName} ({recipientEmail})");
                        successCount++;
                    }
                    else
                    {
                        _logger.LogError($"  FAILED - Could not send to {companyName} ({recipientEmail})");
                        failCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"  ERROR processing {companyName}: {ex.Message}");
                    failCount++;
                }
            }

            _logger.LogInformation("========================================");
            _logger.LogInformation($"MONTHLY REPORT COMPLETE: {successCount} sent / {failCount} failed");
            _logger.LogInformation("========================================");
        }

        /// <summary>
        /// Line-for-line copy of CompanyWiseReportController.GenerateCompanyWiseReportBytes.
        /// Every SQL query, every column header string, every cell value expression, every style
        /// call is IDENTICAL to the controller. The only difference: instead of from/to/month/year
        /// coming from a web request, month and year are passed in as the previous calendar month,
        /// with from=null and to=null — which is exactly how the controller behaves when a user
        /// selects a month from the dropdown (the SQL uses MONTH() and YEAR() conditions).
        /// </summary>
        private async Task<byte[]> GenerateCompanyWiseReportBytes(
            string connStr, string company, int month, int year)
        {
            try
            {
                using var conn = new SqlConnection(connStr);

                // Get card type remarks
                var cardTypes = conn.Query("SELECT card_type, Remark FROM db_tbl_10_cards_master").ToList();

                // Get Top-UP data
                string topUpSql = @"
                    SELECT t.created_on as payment_date, t.paidamount as payment_amount,
                           t.payment_source as payment_mode, t.Remark as remark
                    FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions] t
                    INNER JOIN [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] sc
                        ON t.car_parkers_sub_co_id = sc.car_parkers_sub_co_id
                    WHERE sc.car_parkers_sub_co_name = @company
                        AND t.transaction_type IN ('Top-UP', 'TOP-UP', 'TOPUP')
                        AND (@from IS NULL OR t.created_on >= @from)
                        AND (@to IS NULL OR t.created_on <= @to)
                        AND (@month IS NULL OR MONTH(t.created_on) = @month)
                        AND (@year IS NULL OR YEAR(t.created_on) = @year)
                    ORDER BY t.created_on DESC";

                var topUpData = (await conn.QueryAsync(topUpSql, new
                {
                    company,
                    from = (DateTime?)null,
                    to = (DateTime?)null,
                    month = (int?)month,
                    year = (int?)year
                })).ToList();

                // Get Company Details
                string subSql = @"
                    SELECT car_parkers_sub_co_name, sub_co_car_parkers_contact_person,
                           sub_co_car_parkers_email, sub_co_car_parkers_contact_number,
                           sub_co_car_parkers_pin, car_parkers_status, applicable_card_type,
                           card_parkers_company_password, Total_Card_Limit_Under_Sub_Co,
                           Total_Cards_Under_Sub_Co, Total_Paid_Amount, Total_Deducted_Amount,
                           Balance_Amount, car_parkers_sub_co_id
                    FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
                    WHERE car_parkers_sub_co_name = @company";

                var subCompany = await conn.QueryFirstOrDefaultAsync(subSql, new { company });

                // Get Cards
                string cardsSql = @"
                    SELECT ca.cardname, ca.card_type, ca.car_plate_no, ca.car_owner_name,
                           ca.car_owner_contact_no, ca.status, ca.expiry_date, ca.access_rights,
                           ISNULL(SUM(tl.total_charge), 0) as total_charge
                    FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated] ca
                    LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_21_TAG_log] tl 
                        ON ca.cardname = tl.card_name
                        AND tl.car_parkers_sub_co_id = @subCoId
                        AND (@from IS NULL OR tl.entry_time >= @from)
                        AND (@to IS NULL OR tl.exit_time <= @to)
                        AND (@month IS NULL OR MONTH(tl.entry_time) = @month)
                        AND (@year IS NULL OR YEAR(tl.entry_time) = @year)
                    WHERE ca.car_parkers_sub_co_id = @subCoId
                    GROUP BY ca.cardname, ca.card_type, ca.car_plate_no, ca.car_owner_name,
                             ca.car_owner_contact_no, ca.status, ca.expiry_date, ca.access_rights";

                var cards = (await conn.QueryAsync(cardsSql, new
                {
                    subCoId = subCompany?.car_parkers_sub_co_id,
                    from = (DateTime?)null,
                    to = (DateTime?)null,
                    month = (int?)month,
                    year = (int?)year
                })).ToList();

                // Get Transactions
                string transSql = @"
                    SELECT t.[id], t.[cardno], t.[car_parkers_sub_co_name], t.[car_plate_no],
                           t.[card_name], t.[card_type], t.[entry_time], t.[exit_time],
                           t.[total_charge], t.[remarks], ca.[car_owner_name],
                           ca.[car_plate_no] as allocated_plate_no
                    FROM [AMAAN_PMS].[dbo].[db_tbl_21_TAG_log] t
                    INNER JOIN [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] sc
                        ON t.car_parkers_sub_co_id = sc.car_parkers_sub_co_id
                    LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated] ca
                        ON t.card_name = ca.cardname
                    WHERE sc.car_parkers_sub_co_name = @company
                        AND (@from IS NULL OR t.entry_time >= @from)
                        AND (@to IS NULL OR t.exit_time <= @to)
                        AND (@month IS NULL OR MONTH(t.entry_time) = @month)
                        AND (@year IS NULL OR YEAR(t.entry_time) = @year)
                    ORDER BY t.entry_time DESC";

                var transactions = (await conn.QueryAsync(transSql, new
                {
                    company,
                    from = (DateTime?)null,
                    to = (DateTime?)null,
                    month = (int?)month,
                    year = (int?)year
                })).ToList();

                // Build Excel using ClosedXML
                using var workbook = new ClosedXML.Excel.XLWorkbook();
                var worksheet = workbook.Worksheets.Add("Company Report");

                int row = 1;

                // Title
                worksheet.Cell(row, 1).Value = $"Company Wise Report - {company}";
                worksheet.Cell(row, 1).Style.Font.Bold = true;
                worksheet.Cell(row, 1).Style.Font.FontSize = 14;
                row += 2;

                // TOP-UP Section
                worksheet.Cell(row, 1).Value = "TOP-UP";
                worksheet.Cell(row, 1).Style.Font.Bold = true;
                row++;

                worksheet.Cell(row, 1).Value = "Payment Date";
                worksheet.Cell(row, 2).Value = "Payment Amount";
                worksheet.Cell(row, 3).Value = "Payment Mode";
                worksheet.Cell(row, 4).Value = "Remark";
                worksheet.Range(row, 1, row, 4).Style.Font.Bold = true;
                worksheet.Range(row, 1, row, 4).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;
                row++;

                foreach (var item in topUpData)
                {
                    worksheet.Cell(row, 1).Value = item.payment_date != null ? ((DateTime)item.payment_date).ToString("dd-MMM-yyyy") : "";
                    worksheet.Cell(row, 2).Value = $"{item.payment_amount ?? 0} KES";
                    worksheet.Cell(row, 3).Value = item.payment_mode ?? "";
                    worksheet.Cell(row, 4).Value = item.remark ?? "";
                    row++;
                }

                row += 2;

                // Company Details Section
                if (subCompany != null)
                {
                    worksheet.Cell(row, 1).Value = "Company Details";
                    worksheet.Cell(row, 1).Style.Font.Bold = true;
                    row++;

                    string[] companyHeaders = { "Sub Company Name", "Contact Person", "Email", "Contact/USER ID",
                        "PIN", "Status", "Applicable Card Type", "Password", "Card Limit", "Total Cards",
                        "Total Paid", "Total Deducted", "Balance" };

                    for (int i = 0; i < companyHeaders.Length; i++)
                    {
                        worksheet.Cell(row, i + 1).Value = companyHeaders[i];
                        worksheet.Cell(row, i + 1).Style.Font.Bold = true;
                        worksheet.Cell(row, i + 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;
                    }
                    row++;

                    worksheet.Cell(row, 1).Value = subCompany.car_parkers_sub_co_name ?? "";
                    worksheet.Cell(row, 2).Value = subCompany.sub_co_car_parkers_contact_person ?? "";
                    worksheet.Cell(row, 3).Value = subCompany.sub_co_car_parkers_email ?? "";
                    worksheet.Cell(row, 4).Value = subCompany.sub_co_car_parkers_contact_number ?? "";
                    worksheet.Cell(row, 5).Value = subCompany.sub_co_car_parkers_pin ?? "";
                    worksheet.Cell(row, 6).Value = subCompany.car_parkers_status == "1" ? "Active" : "Inactive";
                    worksheet.Cell(row, 7).Value = subCompany.applicable_card_type ?? "";
                    worksheet.Cell(row, 8).Value = subCompany.card_parkers_company_password ?? "";
                    worksheet.Cell(row, 9).Value = subCompany.Total_Card_Limit_Under_Sub_Co ?? "";
                    worksheet.Cell(row, 10).Value = subCompany.Total_Cards_Under_Sub_Co ?? "";
                    worksheet.Cell(row, 11).Value = subCompany.Total_Paid_Amount ?? "";
                    worksheet.Cell(row, 12).Value = subCompany.Total_Deducted_Amount ?? "";
                    worksheet.Cell(row, 13).Value = subCompany.Balance_Amount ?? "";
                    worksheet.Range(row, 1, row, 13).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#90EE90");
                    row += 2;
                }

                // Card Details Section
                worksheet.Cell(row, 1).Value = "Card Details";
                worksheet.Cell(row, 1).Style.Font.Bold = true;
                row++;

                string[] cardHeaders = { "#", "Card Name", "Card Type", "Plate No", "Owner Name",
                    "Owner Phone", "Card Status", "Expiry Date", "Access Rights", "Total Charge" };

                for (int i = 0; i < cardHeaders.Length; i++)
                {
                    worksheet.Cell(row, i + 1).Value = cardHeaders[i];
                    worksheet.Cell(row, i + 1).Style.Font.Bold = true;
                    worksheet.Cell(row, i + 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;
                }
                row++;

                int cardNum = 1;
                foreach (var card in cards)
                {
                    var cardTypeMaster = cardTypes.FirstOrDefault(ct => ct.card_type == card.card_type);
                    string cardTypeRemark = cardTypeMaster?.Remark ?? card.card_type;

                    worksheet.Cell(row, 1).Value = cardNum++;
                    worksheet.Cell(row, 2).Value = card.cardname ?? "";
                    worksheet.Cell(row, 3).Value = cardTypeRemark ?? "";
                    worksheet.Cell(row, 4).Value = card.car_plate_no ?? "";
                    worksheet.Cell(row, 5).Value = card.car_owner_name ?? "";
                    worksheet.Cell(row, 6).Value = card.car_owner_contact_no ?? "";
                    worksheet.Cell(row, 7).Value = card.status == "1" ? "Active" : "Inactive";
                    worksheet.Cell(row, 8).Value = card.expiry_date != null ? ((DateTime)card.expiry_date).ToString("dd-MMM-yyyy") : "";
                    worksheet.Cell(row, 9).Value = card.access_rights ?? "";
                    worksheet.Cell(row, 10).Value = $"{card.total_charge ?? 0} KES";
                    row++;
                }

                row += 2;

                // Detailed Transaction Report
                worksheet.Cell(row, 1).Value = "Detailed Transaction Report";
                worksheet.Cell(row, 1).Style.Font.Bold = true;
                row++;

                string[] transHeaders = { "#", "ID", "Card Name", "Owner Name", "Plate No", "Card Type",
                    "Entry Date", "Entry Time", "Exit Date", "Exit Time", "Park Time", "Charge", "Remarks" };

                for (int i = 0; i < transHeaders.Length; i++)
                {
                    worksheet.Cell(row, i + 1).Value = transHeaders[i];
                    worksheet.Cell(row, i + 1).Style.Font.Bold = true;
                    worksheet.Cell(row, i + 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;
                }
                row++;

                int transNum = 1;
                foreach (var trans in transactions)
                {
                    var cardTypeMaster = cardTypes.FirstOrDefault(ct => ct.card_type == trans.card_type);
                    string cardTypeRemark = cardTypeMaster?.Remark ?? trans.card_type;

                    DateTime? entryTime = trans.entry_time;
                    DateTime? exitTime = trans.exit_time;

                    string parkTime = "";
                    if (entryTime.HasValue && exitTime.HasValue)
                    {
                        var span = exitTime.Value - entryTime.Value;
                        parkTime = string.Format("{0:D2}:{1:D2}:{2:D2}", (int)span.TotalHours, span.Minutes, span.Seconds);
                    }

                    worksheet.Cell(row, 1).Value = transNum++;
                    worksheet.Cell(row, 2).Value = trans.id;
                    worksheet.Cell(row, 3).Value = trans.card_name ?? "";
                    worksheet.Cell(row, 4).Value = trans.car_owner_name ?? "";
                    worksheet.Cell(row, 5).Value = trans.allocated_plate_no ?? trans.car_plate_no ?? "";
                    worksheet.Cell(row, 6).Value = cardTypeRemark ?? "";
                    worksheet.Cell(row, 7).Value = entryTime.HasValue ? entryTime.Value.ToString("dd-MMM-yyyy") : "";
                    worksheet.Cell(row, 8).Value = entryTime.HasValue ? entryTime.Value.ToString("HH:mm:ss") : "";
                    worksheet.Cell(row, 9).Value = exitTime.HasValue ? exitTime.Value.ToString("dd-MMM-yyyy") : "";
                    worksheet.Cell(row, 10).Value = exitTime.HasValue ? exitTime.Value.ToString("HH:mm:ss") : "";
                    worksheet.Cell(row, 11).Value = parkTime;
                    worksheet.Cell(row, 12).Value = trans.total_charge ?? 0;
                    worksheet.Cell(row, 13).Value = trans.remarks ?? "";
                    row++;
                }

                // Auto-fit columns
                worksheet.Columns().AdjustToContents();

                // Apply borders to all used cells
                var usedRange = worksheet.RangeUsed();
                if (usedRange != null)
                {
                    usedRange.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                    usedRange.Style.Border.InsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                }

                using var stream = new MemoryStream();
                workbook.SaveAs(stream);
                return stream.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error generating Excel report for {company}: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Sends the report email using the same SMTP pattern as CompanyWiseReportController.
        /// Subject format mirrors the controller: "Company Wise Report - {companyName}".
        /// </summary>
        private async Task<bool> SendEmailWithAttachment(
            dynamic smtpConfig,
            string recipientEmail,
            string companyName,
            byte[] attachmentBytes,
            int month,
            int year,
            string connStr = null)
        {
            try
            {
                using var client = new SmtpClient((string)smtpConfig.smtp_server, (int)smtpConfig.smtp_port)
                {
                    Credentials = new NetworkCredential(
                        (string)smtpConfig.smtp_username,
                        (string)smtpConfig.smtp_password),
                    EnableSsl = (bool)smtpConfig.enable_ssl,
                    Timeout = 60000
                };

                string reportMonthLabel = new DateTime(year, month, 1).ToString("MMMM yyyy");

                using var message = new MailMessage
                {
                    From = new MailAddress(
                        (string)smtpConfig.from_email,
                        (string)smtpConfig.from_name),
                    Subject = $"Company Wise Report - {companyName} - {reportMonthLabel}",
                    Body = BuildEmailBody(companyName, month, year),
                    IsBodyHtml = true
                };

                message.To.Add(recipientEmail);

                string fileName = $"CompanyReport_{companyName.Replace(" ", "_")}_{year}_{month:D2}.xlsx";
                var attachment = new Attachment(
                    new MemoryStream(attachmentBytes),
                    fileName,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                message.Attachments.Add(attachment);

                await client.SendMailAsync(message);
                await LogToEmailTableAsync(connStr, recipientEmail, companyName, $"Company Wise Report - {companyName}", "Monthly-Report", "Sent", null);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"SMTP send error to {recipientEmail}: {ex.Message}");
                await LogToEmailTableAsync(connStr, recipientEmail, companyName, $"Company Wise Report - {companyName}", "Monthly-Report", "Failed", ex.Message);
                return false;
            }
        }

        private string BuildEmailBody(string companyName, int month, int year)
        {
            string reportMonthLabel = new DateTime(year, month, 1).ToString("MMMM yyyy");

            return $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h3>Dear {companyName},</h3>
                    <p>Please find attached your Company Wise Report for {reportMonthLabel}.</p>
                    <p>This report includes:</p>
                    <ul>
                        <li>TOP-UP Payments</li>
                        <li>Company Details</li>
                        <li>Card Details</li>
                        <li>Detailed Transaction Report</li>
                    </ul>
                    <p>If you have any questions, please contact our support team.</p>
                    <br/>
                    <p>Best Regards,<br/>Amaan Parking System</p>
                </body>
                </html>";
        }
        private async Task LogToEmailTableAsync(string connStr, string recipientEmail, string companyName, string subject, string emailType, string status, string errorMessage)
        {
            try
            {
                using var conn = new SqlConnection(connStr);
                await conn.ExecuteAsync(
                    @"INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_29_email_logs]
                        (EmailType, RecipientEmail, CCEmail, Subject, CompanyId, CompanyName, SiteId, SiteName, Status, ErrorMessage, CreatedOn)
                      VALUES
                        (@EmailType, @RecipientEmail, NULL, @Subject, NULL, @CompanyName, NULL, NULL, @Status, @ErrorMessage, GETDATE())",
                    new { EmailType = emailType, RecipientEmail = recipientEmail, CompanyName = companyName, Subject = subject, Status = status, ErrorMessage = errorMessage });
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"LogToEmailTableAsync failed (non-fatal): {ex.Message}");
            }
        }
    }
}