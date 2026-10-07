using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System;

namespace AmaanParkingSystem.Controllers
{
    public class CompanyWiseReportController : BaseController
    {
        public CompanyWiseReportController()
        {
        }

        [HttpGet]
        public IActionResult Index() => View("~/Views/Reports/CompanyWiseReport.cshtml");

        // Search autocomplete from parker company table
        [HttpGet]
        public async Task<IActionResult> SearchCompanies(string q)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());
            string sql = @"
                SELECT DISTINCT TOP 20
                car_parkers_sub_co_name as company_name
                FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
                WHERE car_parkers_sub_co_name LIKE @Q
                AND car_parkers_status = '1'
                ORDER BY car_parkers_sub_co_name";
            var results = await conn.QueryAsync(sql, new { Q = "%" + q + "%" });
            return Json(results.Select(r => new { company_name = r.company_name }));
        }

        // Get Top-UP Payments - UPDATED: Added Remark column
        [HttpPost]
        public async Task<IActionResult> GetTopUpPayments([FromBody] CompanyWiseReportRequest req)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());
            string sql = @"
                SELECT
                t.created_on as payment_date,
                t.paidamount as payment_amount,
                t.payment_source as payment_mode,
                t.cardname as card_name,
                t.Remark as remark
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

            var data = await conn.QueryAsync(sql, new
            {
                company = req.Company,
                from = req.From,
                to = req.To,
                month = req.Month,
                year = req.Year
            });
            return Json(data);
        }

        // Get Company Details - UPDATED: Added total_charge calculation from TAG_log
        [HttpPost]
        public async Task<IActionResult> GetCompanyDetails([FromBody] CompanyWiseReportRequest req)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            // Get card type remarks from Card Master table
            var cardTypes = conn.Query(
                "SELECT card_type, Remark FROM db_tbl_10_cards_master").ToList();

            // Get Sub Company Details
            string subSql = @"
        SELECT
        car_parkers_sub_co_name,
        sub_co_car_parkers_contact_person,
        sub_co_car_parkers_email,
        sub_co_car_parkers_contact_number,
        sub_co_car_parkers_pin,
        car_parkers_status,
        applicable_card_type,
        card_parkers_company_password,
        Total_Card_Limit_Under_Sub_Co,
        Total_Cards_Under_Sub_Co,
        Total_Paid_Amount,
        Total_Deducted_Amount,
        Balance_Amount,
        car_parkers_sub_co_id
        FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
        WHERE car_parkers_sub_co_name = @company";

            var subCompany = await conn.QueryFirstOrDefaultAsync(subSql, new { company = req.Company });

            if (subCompany == null)
                return Json(new { subCompany = (object)null, cards = new List<dynamic>() });

            // Get Cards for this sub company with total_charge
            string cardsSql = @"
        SELECT
        ca.cardname,
        ca.card_type,
        ca.car_plate_no,
        ca.car_owner_name,
        ca.car_owner_contact_no,
        ca.status,
        ca.expiry_date,
        ca.access_rights,
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
                subCoId = subCompany.car_parkers_sub_co_id,
                from = req.From,
                to = req.To,
                month = req.Month,
                year = req.Year
            })).ToList();

            // Map card type remark for each card
            foreach (var card in cards)
            {
                var cardTypeMaster = cardTypes.FirstOrDefault(ct => ct.card_type == card.card_type);
                card.card_type_remark = cardTypeMaster?.Remark ?? card.card_type;
            }

            return Json(new { subCompany, cards });
        }

        // Get Detailed Transaction Report - UPDATED: Added Owner name and car plate number
        [HttpPost]
        public async Task<IActionResult> GetReport([FromBody] CompanyWiseReportRequest req)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            // Card type → remark map
            var cardTypes = conn.Query(
                "SELECT card_type, Remark FROM db_tbl_10_cards_master")
                .ToDictionary(
                    x => (string)x.card_type,
                    x => (string)x.Remark
                );

            string sql = @"
                SELECT TOP (1000000)
                t.[id]
                , t.[cardno]
                , t.[car_parkers_sub_co_name]
                , t.[car_plate_no]
                , t.[card_name]
                , t.[card_type]
                , t.[entry_time]
                , t.[exit_time]
                , t.[total_charge]
                , t.[remarks]
                , ca.[car_owner_name]
                , ca.[car_plate_no] as allocated_plate_no
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

            var rows = (await conn.QueryAsync(sql, new
            {
                company = req.Company,
                from = req.From,
                to = req.To,
                month = req.Month,
                year = req.Year
            })).ToList();

            foreach (var row in rows)
            {
                // Card type remark
                string ct = row.card_type;
                if (!string.IsNullOrEmpty(ct) && cardTypes.ContainsKey(ct))
                    row.card_type_remark = cardTypes[ct];
                else
                    row.card_type_remark = ct;

                // Park time HH:MM:SS from entry/exit
                if (row.entry_time != null && row.exit_time != null)
                {
                    DateTime entry = (DateTime)row.entry_time;
                    DateTime exit = (DateTime)row.exit_time;
                    var span = exit - entry;
                    row.park_time_total = string.Format("{0:D2}:{1:D2}:{2:D2}",
                        (int)span.TotalHours, span.Minutes, span.Seconds);
                }
                else
                {
                    row.park_time_total = "";
                }

                // Convenience fields for UI + export
                var entryDt = (DateTime)row.entry_time;
                row.entry_date = entryDt.ToString("dd-MMM-yyyy");
                row.entry_time_only = entryDt.ToString("HH:mm:ss");

                if (row.exit_time != null)
                {
                    var exitDt = (DateTime)row.exit_time;
                    row.exit_date = exitDt.ToString("dd-MMM-yyyy");
                    row.exit_time_only = exitDt.ToString("HH:mm:ss");
                }
                else
                {
                    row.exit_date = "";
                    row.exit_time_only = "";
                }

                // Charge from total_charge
                row.charges = row.total_charge;
            }

            return Json(rows);
        }

        // ========== NEW EMAIL METHODS START HERE ==========

        [HttpPost]
        public async Task<IActionResult> SendCompanyWiseReportEmail([FromBody] EmailReportRequest req)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());

                // Get company email from parkers_company table
                string emailSql = @"
                    SELECT sub_co_car_parkers_email, car_parkers_sub_co_name
                    FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
                    WHERE car_parkers_sub_co_name = @Company
                    AND car_parkers_status = '1'";

                var companyInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(emailSql, new { Company = req.Company });

                if (companyInfo == null)
                {
                    return Json(new { success = false, message = "Company not found" });
                }

                string recipientEmail = companyInfo.sub_co_car_parkers_email;

                if (string.IsNullOrWhiteSpace(recipientEmail))
                {
                    return Json(new { success = false, message = "No email address found for this company" });
                }

                // Generate Excel report using existing export logic
                byte[] excelBytes = await GenerateCompanyWiseReportBytes(req);

                if (excelBytes == null || excelBytes.Length == 0)
                {
                    return Json(new { success = false, message = "Failed to generate report" });
                }

                // Get SMTP configuration from database
                var smtpConfig = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT TOP 1 smtp_server, smtp_port, smtp_username, smtp_password, 
                           from_email, from_name, enable_ssl
                    FROM [AMAAN_PMS].[dbo].[db_tbl_22_email_config]
                    WHERE is_active = 1
                    ORDER BY id DESC");

                if (smtpConfig == null)
                {
                    return Json(new { success = false, message = "Email configuration not found" });
                }

                // Send email
                bool emailSent = await SendEmailWithAttachment(
                    smtpConfig,
                    recipientEmail,
                    companyInfo.car_parkers_sub_co_name,
                    excelBytes,
                    req
                );

                if (emailSent)
                {
                    return Json(new { success = true, message = $"Report sent to {recipientEmail}" });
                }
                else
                {
                    return Json(new { success = false, message = "Failed to send email" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        private async Task<byte[]> GenerateCompanyWiseReportBytes(EmailReportRequest req)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());

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
                    company = req.Company,
                    from = req.From,
                    to = req.To,
                    month = req.Month,
                    year = req.Year
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

                var subCompany = await conn.QueryFirstOrDefaultAsync(subSql, new { company = req.Company });

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
                    from = req.From,
                    to = req.To,
                    month = req.Month,
                    year = req.Year
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
                    company = req.Company,
                    from = req.From,
                    to = req.To,
                    month = req.Month,
                    year = req.Year
                })).ToList();

                // Build Excel using ClosedXML
                using var workbook = new ClosedXML.Excel.XLWorkbook();
                var worksheet = workbook.Worksheets.Add("Company Report");

                int row = 1;

                // Title
                worksheet.Cell(row, 1).Value = $"Company Wise Report - {req.Company}";
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
                // Log error
                return null;
            }
        }

        private async Task<bool> SendEmailWithAttachment(
            dynamic smtpConfig,
            string recipientEmail,
            string companyName,
            byte[] attachmentBytes,
            EmailReportRequest req)
        {
            try
            {
                using var client = new System.Net.Mail.SmtpClient((string)smtpConfig.smtp_server, (int)smtpConfig.smtp_port)
                {
                    Credentials = new System.Net.NetworkCredential(
                        (string)smtpConfig.smtp_username,
                        (string)smtpConfig.smtp_password),
                    EnableSsl = (bool)smtpConfig.enable_ssl
                };

                using var message = new System.Net.Mail.MailMessage
                {
                    From = new System.Net.Mail.MailAddress(
                        (string)smtpConfig.from_email,
                        (string)smtpConfig.from_name),
                    Subject = $"Company Wise Report - {companyName}",
                    Body = BuildEmailBody(companyName, req),
                    IsBodyHtml = true
                };

                message.To.Add(recipientEmail);

                string fileName = $"CompanyReport_{companyName.Replace(" ", "_")}_{DateTime.Now:ddMMMyyyy}.xlsx";
                var attachment = new System.Net.Mail.Attachment(
                    new MemoryStream(attachmentBytes),
                    fileName,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                message.Attachments.Add(attachment);

                await client.SendMailAsync(message);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string BuildEmailBody(string companyName, EmailReportRequest req)
        {
            string dateRange = "";
            if (!string.IsNullOrEmpty(req.From) && !string.IsNullOrEmpty(req.To))
            {
                dateRange = $"From {req.From} To {req.To}";
            }
            else if (req.Month.HasValue && req.Year.HasValue)
            {
                dateRange = $"For {new DateTime(req.Year.Value, req.Month.Value, 1):MMMM yyyy}";
            }
            else if (req.Year.HasValue)
            {
                dateRange = $"For Year {req.Year.Value}";
            }

            return $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h3>Dear {companyName},</h3>
                    <p>Please find attached your Company Wise Report {dateRange}.</p>
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
    }

    public class CompanyWiseReportRequest
    {
        public string Company { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public int? Month { get; set; }
        public int? Year { get; set; }
    }

    public class EmailReportRequest
    {
        public string Company { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public int? Month { get; set; }
        public int? Year { get; set; }
    }
}
