using AmaanParkingSystem.Services;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;


namespace AmaanParkingSystem.Controllers
{
    public class LowBalanceAlertsController : BaseController
    {
        private readonly EmailReportService _emailService;
        private readonly ILogger<LowBalanceAlertsController> _logger;

        public LowBalanceAlertsController(
            EmailReportService emailService,
            ILogger<LowBalanceAlertsController> logger)
        {
            _emailService = emailService;
            _logger = logger;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public IActionResult EnableAutomaticNotifications([FromBody] AutoNotificationControlRequest request)
        {
            try
            {
                _emailService.SetAutomaticNotificationsEnabled(request.Enable);

                string message = request.Enable
                    ? "✅ Automatic low balance notifications ENABLED. System will check every 6 hours."
                    : "⏸️ Automatic low balance notifications DISABLED. Manual checks only.";

                _logger.LogInformation(message);

                return Json(new
                {
                    success = true,
                    message = message,
                    enabled = request.Enable
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAutomaticNotificationStatus()
        {
            try
            {
                var status = await _emailService.GetAutomaticNotificationStatusAsync();
                return Json(new { success = true, data = status });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCardTypes()
        {
            try
            {
                using (var connection = new SqlConnection(GetDynamicConnectionString()))
                {
                    await connection.OpenAsync();

                    // Get card types from Remark column
                    var query = @"SELECT DISTINCT [Remark] 
                          FROM [AMAAN_PMS].[dbo].[db_tbl_10_cards_master]
                          WHERE [Remark] IS NOT NULL 
                            AND [Remark] != ''
                          ORDER BY [Remark]";

                    var cardTypes = await connection.QueryAsync<string>(query);

                    _logger.LogInformation($"GetCardTypes: Found {cardTypes.Count()} card types from Remark column");

                    return Json(new { success = true, data = cardTypes });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"GetCardTypes Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SendLowBalanceNotifications([FromForm] LowBalanceRequest request)
        {
            try
            {
                decimal threshold = request?.Threshold ?? 500m;
                byte[] attachmentBytes = null;
                string attachmentName = null;

                if (request.Attachment != null && request.Attachment.Length > 0)
                {
                    using var ms = new MemoryStream();
                    await request.Attachment.CopyToAsync(ms);
                    attachmentBytes = ms.ToArray();
                    attachmentName = request.Attachment.FileName;
                }

                var success = await _emailService.SendSubCompanyLowBalanceNotificationsAsync(
                    threshold, request.Subject, request.Body, attachmentBytes, attachmentName);

                return Json(new { success, message = success ? "Sent successfully." : "Failed." });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        [HttpPost]
        public async Task<IActionResult> SendLowBalanceNotificationsToSelected([FromForm] SelectedCompaniesRequest request)
        {
            try
            {
                if (request.CompanyIds == null || !request.CompanyIds.Any())
                    return Json(new { success = false, message = "No companies selected." });

                byte[] attachmentBytes = null;
                string attachmentName = null;

                if (request.Attachment != null && request.Attachment.Length > 0)
                {
                    using var ms = new MemoryStream();
                    await request.Attachment.CopyToAsync(ms);
                    attachmentBytes = ms.ToArray();
                    attachmentName = request.Attachment.FileName;
                }

                int successCount = 0, failCount = 0;

                foreach (var companyId in request.CompanyIds)
                {
                    var success = await _emailService.SendSingleCompanyLowBalanceNotificationAsync(
                        companyId, request.Threshold, request.Subject, request.Body, attachmentBytes, attachmentName);

                    if (success)
                    {
                        successCount++;
                        await UpdateLastNotifiedDate(companyId);
                    }
                    else failCount++;
                }

                return Json(new
                {
                    success = true,
                    message = $"Sent: {successCount} successful, {failCount} failed.",
                    successCount,
                    failCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"SendToSelected Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SendLowBalanceNotificationToCompany([FromForm] SingleCompanyNotificationRequest request)
        {
            try
            {
                byte[] fileBytes = null;
                string fileName = null;

                if (request.Attachment != null && request.Attachment.Length > 0)
                {
                    using var ms = new MemoryStream();
                    await request.Attachment.CopyToAsync(ms);
                    fileBytes = ms.ToArray();
                    fileName = request.Attachment.FileName;
                }

                var success = await _emailService.SendSingleCompanyLowBalanceNotificationAsync(
                    request.SubCompanyId, request.Threshold, request.Subject, request.Body, fileBytes, fileName);

                if (success) await UpdateLastNotifiedDate(request.SubCompanyId);

                return Json(new { success, message = success ? "Email sent" : "Failed" });
            }
            catch (Exception ex)
            {
                _logger.LogError($"SendToCompany Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        private async Task UpdateLastNotifiedDate(int companyId)
        {
            try
            {
                using (var connection = new SqlConnection(GetDynamicConnectionString()))
                {
                    await connection.OpenAsync();
                    var query = @"UPDATE [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
                          SET [Last_Notified_Date] = GETDATE()
                          WHERE [id] = @CompanyId";
                    await connection.ExecuteAsync(query, new { CompanyId = companyId });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"UpdateLastNotified Error: {ex.Message}");
            }
        }
        [HttpGet]
        public async Task<IActionResult> GetLowBalanceSubCompanies([FromQuery] decimal? threshold, [FromQuery] string cardType = "All")
        {
            try
            {
                decimal balanceThreshold = threshold ?? 500m;

                var lowBalanceCompanies = await _emailService.GetLowBalanceSubCompaniesWithCacheAsync(balanceThreshold, cardType);

                _logger.LogInformation("Retrieved {Count} low balance companies (threshold: {Threshold})", lowBalanceCompanies.Count, balanceThreshold);

                if (!string.IsNullOrEmpty(cardType) && cardType != "All")
                {
                    lowBalanceCompanies = lowBalanceCompanies
                        .Where(c => string.Equals((string?)c.applicablecardtype, cardType, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    _logger.LogInformation("After filtering by card type '{CardType}', {Count} companies remain", cardType, lowBalanceCompanies.Count);
                }

                return Json(new
                {
                    success = true,
                    data = lowBalanceCompanies
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetLowBalance Error");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetBalanceSummary([FromQuery] decimal? threshold, [FromQuery] string cardType = "All")
        {
            try
            {
                decimal balanceThreshold = threshold ?? 500m;

                using (var connection = new SqlConnection(GetDynamicConnectionString()))
                {
                    await connection.OpenAsync();

                    var query = @"
                SELECT 
                    COUNT(*) AS TotalSubCompanies,
                    SUM(CASE WHEN ISNULL(c.[Balance_Amount], 0) < @Threshold THEN 1 ELSE 0 END) AS LowBalanceCount,
                    SUM(CASE WHEN ISNULL(c.[Balance_Amount], 0) < 0 THEN 1 ELSE 0 END) AS NegativeBalanceCount,
                    SUM(CASE WHEN ISNULL(c.[Balance_Amount], 0) = 0 THEN 1 ELSE 0 END) AS ZeroBalanceCount,
                    SUM(CASE WHEN ISNULL(c.[Balance_Amount], 0) >= @Threshold THEN 1 ELSE 0 END) AS HealthyBalanceCount,
                    ISNULL(SUM(CASE WHEN ISNULL(c.[Balance_Amount], 0) > 0 THEN c.[Balance_Amount] ELSE 0 END), 0) AS TotalDeposit,
                    ISNULL(ABS(SUM(CASE WHEN ISNULL(c.[Balance_Amount], 0) < 0 THEN c.[Balance_Amount] ELSE 0 END)), 0) AS ToBeCollected
                FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] c
                LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_10_cards_master] cm
                    ON c.[applicable_card_type] = cm.[id]
                WHERE c.[car_parkers_status] = 1";

                    if (!string.IsNullOrWhiteSpace(cardType) && cardType != "All")
                    {
                        query += " AND cm.[Remark] = @CardType";
                    }

                    var summary = await connection.QueryFirstOrDefaultAsync(query, new
                    {
                        Threshold = balanceThreshold,
                        CardType = cardType
                    });

                    return Json(new { success = true, data = summary });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"GetSummary Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetNegativeBalanceCount()
        {
            try
            {
                using (var connection = new SqlConnection(GetDynamicConnectionString()))
                {
                    await connection.OpenAsync();
                    var count = await connection.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] WHERE [Balance_Amount] < 0");
                    return Json(new { success = true, data = count });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"GetNegative Error: {ex.Message}");
                return Json(new { success = false, message = $"Error: {ex.Message}" });
            }
        }
        [HttpGet]
        public async Task<IActionResult> ExportLowBalanceReport([FromQuery] decimal? threshold, [FromQuery] string cardType = "All")
        {
            try
            {
                decimal balanceThreshold = threshold ?? 500m;

                using (var connection = new SqlConnection(GetDynamicConnectionString()))
                {
                    await connection.OpenAsync();

                    var query = @"
                SELECT 
                    c.[id],
                    c.[car_parkers_sub_co_id],
                    c.[car_parkers_sub_co_name],
                    c.[sub_co_car_parkers_contact_person],
                    c.[sub_co_car_parkers_contact_number],
                    c.[sub_co_car_parkers_email],
                    ISNULL(c.[Balance_Amount], 0) AS current_balance,
                    c.[NegBalanceallowed],
                    c.[Last_Notified_Date],
                    cm.[Remark] AS applicable_card_type
                FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] c
                LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_10_cards_master] cm
                    ON c.[applicable_card_type] = cm.[id]
                WHERE ISNULL(c.[Balance_Amount], 0) < @Threshold";

                    if (!string.IsNullOrEmpty(cardType) && cardType != "All")
                    {
                        query += " AND cm.[Remark] = @CardType";
                    }

                    query += " ORDER BY c.[Balance_Amount] ASC, c.[car_parkers_sub_co_name] ASC";

                    var lowBalanceCompanies = (await connection.QueryAsync(query, new { Threshold = balanceThreshold, CardType = cardType })).ToList();

                    using var workbook = new XLWorkbook();
                    var sheet = workbook.Worksheets.Add("Low Balance Report");

                    string[] headers = { "ID", "Sub Company", "Contact Person", "Contact Number", "Email", "Balance", "Neg Balance Allowed", "Last Notified" };
                    for (int i = 0; i < headers.Length; i++)
                    {
                        var cell = sheet.Cell(1, i + 1);
                        cell.Value = headers[i];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.LightGray;
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
                        negCell.Style.Font.FontColor = XLColor.White;
                        negCell.Style.Fill.BackgroundColor = negAllowed ? XLColor.Green : XLColor.Red;

                        sheet.Cell(rowIndex, 8).Value = company.Last_Notified_Date != null ? Convert.ToDateTime(company.Last_Notified_Date).ToString("dd-MMM-yyyy hh:mm:ss tt") : "Never";
                        rowIndex++;
                    }

                    sheet.Columns().AdjustToContents();

                    using var ms = new MemoryStream();
                    workbook.SaveAs(ms);
                    ms.Position = 0;

                    var fileName = $"LowBalanceReport_{DateTime.Now:yyyy-MM-dd_HH-mm}.xlsx";
                    return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("ExportLowBalanceReport FAILED: {Message}", ex.Message);
                return Json(new { success = false, message = ex.Message });
            }
        }

        public class LowBalanceRequest
        {
            public decimal Threshold { get; set; } = 500m;
            public string Subject { get; set; }
            public string Body { get; set; }
            public IFormFile Attachment { get; set; }
        }

        public class SelectedCompaniesRequest
        {
            public List<int> CompanyIds { get; set; }
            public decimal Threshold { get; set; } = 500m;
            public string Subject { get; set; }
            public string Body { get; set; }
            public IFormFile Attachment { get; set; }
        }

        public class AutoNotificationControlRequest
        {
            public bool Enable { get; set; }
        }

        public class SingleCompanyNotificationRequest
        {
            public int SubCompanyId { get; set; }
            public decimal Threshold { get; set; } = 500m;
            public string Subject { get; set; }
            public string Body { get; set; }
            public IFormFile Attachment { get; set; }
        }
    }
}
