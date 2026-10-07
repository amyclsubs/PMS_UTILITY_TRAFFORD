using AmaanParkingSystem.Controllers;
using AmaanParkingSystem.Models.Reports;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public class ReportsController : BaseController
{
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(ILogger<ReportsController> logger)
    {
        _logger = logger;
    }
    // Manual Gate Report
    [HttpGet]
    public IActionResult ManualGateReport()
    {
        try
        {
            return View("~/Views/Reports/ManualGateReport.cshtml");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading manual gate report page");
            return StatusCode(500, "Error loading report page");
        }
    }

    [HttpPost]
    public IActionResult GetManualGateReportData(DateTime? startDate, DateTime? endDate, string filterType = "today")
    {
        try
        {
            DateTime start, end;

            switch (filterType.ToLower())
            {
                case "today":
                    start = DateTime.Today;
                    end = DateTime.Today.AddDays(1).AddSeconds(-1);
                    break;
                case "yesterday":
                    start = DateTime.Today.AddDays(-1);
                    end = DateTime.Today.AddSeconds(-1);
                    break;
                case "lastweek":
                    // Mon–Sun of the previous calendar week
                    var today = DateTime.Today;
                    int diff = (int)today.DayOfWeek - (int)DayOfWeek.Monday;
                    if (diff < 0) diff += 7;
                    start = today.AddDays(-diff - 7);   // last Monday
                    end = start.AddDays(7).AddSeconds(-1); // last Sunday 23:59:59
                    break;

                case "lastmonth":
                    // 1st to last day of the previous calendar month
                    var firstOfThisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                    start = firstOfThisMonth.AddMonths(-1);               // 1st of last month
                    end = firstOfThisMonth.AddSeconds(-1);              // last day 23:59:59
                    break;
                case "daterange":
                    if (!startDate.HasValue || !endDate.HasValue)
                    {
                        return Json(new { success = false, message = "Please select valid date range" });
                    }
                    start = startDate.Value.Date;
                    end = endDate.Value.Date.AddDays(1).AddSeconds(-1);
                    break;
                default:
                    start = DateTime.Today;
                    end = DateTime.Today.AddDays(1).AddSeconds(-1);
                    break;
            }

            using var conn = new SqlConnection(GetDynamicConnectionString());

            var query = @"
            SELECT 
                [id],
                [ip] AS device,
                [operation],
                [remark_manual_door] AS remark,
                [created_by],
                [created_on]
            FROM [AMAAN_PMS].[dbo].[db_tbl_18_manual_door]
            WHERE [created_on] >= @StartDate AND [created_on] <= @EndDate
            ORDER BY [created_on] DESC";

            var data = conn.Query<ManualGateReportModel>(query, new { StartDate = start, EndDate = end }).ToList();

            return Json(new
            {
                success = true,
                data = data,
                recordCount = data.Count,
                dateRange = $"{start:dd-MMM-yyyy} to {end:dd-MMM-yyyy}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading manual gate report data");
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public IActionResult ExportManualGateReport(DateTime? startDate, DateTime? endDate, string filterType = "today")
    {
        try
        {
            DateTime start, end;

            switch (filterType.ToLower())
            {
                case "today":
                    start = DateTime.Today;
                    end = DateTime.Today.AddDays(1).AddSeconds(-1);
                    break;
                case "yesterday":
                    start = DateTime.Today.AddDays(-1);
                    end = DateTime.Today.AddSeconds(-1);
                    break;
                case "daterange":
                    if (!startDate.HasValue || !endDate.HasValue)
                    {
                        return BadRequest("Please select valid date range");
                    }
                    start = startDate.Value.Date;
                    end = endDate.Value.Date.AddDays(1).AddSeconds(-1);
                    break;
                case "lastweek":
                    // Mon–Sun of the previous calendar week
                    var today = DateTime.Today;
                    int diff = (int)today.DayOfWeek - (int)DayOfWeek.Monday;
                    if (diff < 0) diff += 7;
                    start = today.AddDays(-diff - 7);   // last Monday
                    end = start.AddDays(7).AddSeconds(-1); // last Sunday 23:59:59
                    break;

                case "lastmonth":
                    // 1st to last day of the previous calendar month
                    var firstOfThisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                    start = firstOfThisMonth.AddMonths(-1);               // 1st of last month
                    end = firstOfThisMonth.AddSeconds(-1);              // last day 23:59:59
                    break;
                default:
                    start = DateTime.Today;
                    end = DateTime.Today.AddDays(1).AddSeconds(-1);
                    break;
            }

            using var conn = new SqlConnection(GetDynamicConnectionString());

            var query = @"
            SELECT 
                [id],
                [ip] AS device,
                [operation],
                [remark_manual_door] AS remark,
                [created_by],
                [created_on]
            FROM [AMAAN_PMS].[dbo].[db_tbl_18_manual_door]
            WHERE [created_on] >= @StartDate AND [created_on] <= @EndDate
            ORDER BY [created_on] DESC";

            var data = conn.Query<ManualGateReportModel>(query, new { StartDate = start, EndDate = end }).ToList();

            var excel = BuildManualGateExcel(data, start, end);
            string filename = $"Manual_Gate_Report_{start:yyyyMMdd}_to_{end:yyyyMMdd}.xlsx";

            return File(excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting manual gate report");
            return StatusCode(500, "Error exporting report");
        }
    }

    private byte[] BuildManualGateExcel(List<ManualGateReportModel> data, DateTime startDate, DateTime endDate)
    {
        IWorkbook workbook = new XSSFWorkbook();
        ISheet sheet = workbook.CreateSheet("Manual Gate Report");

        // Title Row
        var titleRow = sheet.CreateRow(0);
        var titleCell = titleRow.CreateCell(0);
        titleCell.SetCellValue($"Manual Gate Report - {startDate:dd-MMM-yyyy} to {endDate:dd-MMM-yyyy}");
        var titleStyle = workbook.CreateCellStyle();
        var titleFont = workbook.CreateFont();
        titleFont.IsBold = true;
        titleFont.FontHeightInPoints = 14;
        titleStyle.SetFont(titleFont);
        titleCell.CellStyle = titleStyle;
        sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(0, 0, 0, 5));

        // Empty row
        sheet.CreateRow(1);

        var headers = new[] {
        "Id", "Device", "Operation", "Remark", "Created By", "Created On"
    };

        // Header Style
        var headerStyle = workbook.CreateCellStyle();
        headerStyle.FillForegroundColor = IndexedColors.Grey40Percent.Index;
        headerStyle.FillPattern = FillPattern.SolidForeground;
        headerStyle.Alignment = HorizontalAlignment.Center;
        headerStyle.VerticalAlignment = VerticalAlignment.Center;
        var headerFont = workbook.CreateFont();
        headerFont.IsBold = true;
        headerFont.Color = IndexedColors.White.Index;
        headerStyle.SetFont(headerFont);

        // Write header
        var headerRow = sheet.CreateRow(2);
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = headerRow.CreateCell(i);
            cell.SetCellValue(headers[i]);
            cell.CellStyle = headerStyle;
        }

        // Data Style
        var dataStyle = workbook.CreateCellStyle();
        dataStyle.Alignment = HorizontalAlignment.Left;
        dataStyle.VerticalAlignment = VerticalAlignment.Center;

        var dateStyle = workbook.CreateCellStyle();
        var dateFormat = workbook.CreateDataFormat();
        dateStyle.DataFormat = dateFormat.GetFormat("dd-mmm-yyyy hh:mm:ss");
        dateStyle.Alignment = HorizontalAlignment.Left;

        // Write data
        int rowNum = 3;
        foreach (var item in data)
        {
            var row = sheet.CreateRow(rowNum++);

            var cell0 = row.CreateCell(0);
            cell0.SetCellValue(item.id);
            cell0.CellStyle = dataStyle;

            var cell1 = row.CreateCell(1);
            cell1.SetCellValue(item.device ?? "-");
            cell1.CellStyle = dataStyle;

            var cell2 = row.CreateCell(2);
            cell2.SetCellValue(item.operation ?? "-");
            cell2.CellStyle = dataStyle;

            var cell3 = row.CreateCell(3);
            cell3.SetCellValue(item.remark ?? "-");
            cell3.CellStyle = dataStyle;

            var cell4 = row.CreateCell(4);
            cell4.SetCellValue(item.created_by ?? "-");
            cell4.CellStyle = dataStyle;

            var cell5 = row.CreateCell(5);
            if (item.created_on.HasValue)
            {
                cell5.SetCellValue(item.created_on.Value);
                cell5.CellStyle = dateStyle;
            }
            else
            {
                cell5.SetCellValue("-");
                cell5.CellStyle = dataStyle;
            }
        }

        // Summary Row
        var summaryRow = sheet.CreateRow(rowNum + 1);
        var summaryCell = summaryRow.CreateCell(0);
        summaryCell.SetCellValue($"Total Records: {data.Count}");
        var summaryStyle = workbook.CreateCellStyle();
        var summaryFont = workbook.CreateFont();
        summaryFont.IsBold = true;
        summaryStyle.SetFont(summaryFont);
        summaryCell.CellStyle = summaryStyle;

        // Auto-size columns
        for (int i = 0; i < headers.Length; i++)
        {
            sheet.AutoSizeColumn(i);
            // Add some padding
            sheet.SetColumnWidth(i, sheet.GetColumnWidth(i) + 1000);
        }

        using var ms = new MemoryStream();
        workbook.Write(ms);
        return ms.ToArray();
    }

    [HttpGet]
public IActionResult TagUsageReport()
    {
        try
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            // ✅ Removed Temporary filter — shows ALL cards including Temporary
            var query = @"
        SELECT 
            c.cardno,
            c.cardname,
            c.card_type,
            cm.Remark as card_type_remark,
            c.car_parkers_company_id,
            c.car_parkers_sub_co_id,
            c.car_parkers_sub_co_name,
            c.car_plate_no,
            c.car_owner_name,
            c.status,
            t.last_entry_time,
            t.last_exit_time,
            t.B1_entry_time,
            t.B1_exit_time,
            t.B2_entry_time,
            t.B2_exit_time,
            t.daily_charge,
            t.penalty_charge,
            t.total_charge,
            t.remarks
        FROM db_tbl_11_cards_allocated c
        LEFT JOIN db_tbl_10_cards_master cm ON c.card_type = cm.card_type
        LEFT JOIN (
            SELECT 
                cardno,
                entry_time as last_entry_time,
                exit_time as last_exit_time,
                B1_entry_time,
                B1_exit_time,
                B2_entry_time,
                B2_exit_time,
                daily_charge,
                penalty_charge,
                total_charge,
                remarks,
                ROW_NUMBER() OVER (PARTITION BY cardno ORDER BY created_on DESC) as rn
            FROM db_tbl_21_TAG_log
        ) t ON c.cardno = t.cardno AND t.rn = 1
        WHERE c.card_type IS NOT NULL
        ORDER BY 
            CASE WHEN t.last_entry_time IS NULL THEN 2 ELSE 1 END,
            t.last_entry_time DESC";

            var data = conn.Query<TagUsageReportModel>(query).ToList();

            // Calculate days since last use and status (unchanged logic)
            foreach (var item in data)
            {
                if (item.last_entry_time.HasValue)
                {
                    item.days_since_last_use = (DateTime.Now - item.last_entry_time.Value).Days;
                    if (item.days_since_last_use <= 7)
                        item.usage_status = "Active";
                    else if (item.days_since_last_use <= 30)
                        item.usage_status = "Recently Used";
                    else
                        item.usage_status = "Inactive";
                }
                else
                {
                    item.usage_status = "Never Used";
                    item.days_since_last_use = null;
                }
            }

            // ✅ Pass distinct card types to ViewBag for the header filter dropdown
            ViewBag.CardTypeList = data
                .Select(x => x.card_type_remark ?? x.card_type)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            return View("~/Views/Reports/TagUsageReport.cshtml", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading tag usage report");
            return StatusCode(500, "Error loading report");
        }
    }

    [HttpGet]
    public IActionResult ExportTagUsageReport()
    {
        try
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            // ✅ Removed Temporary filter — exports ALL cards
            var query = @"
        SELECT 
            c.cardno,
            c.cardname,
            c.card_type,
            cm.Remark as card_type_remark,
            c.car_parkers_company_id,
            c.car_parkers_sub_co_id,
            c.car_parkers_sub_co_name,
            c.car_plate_no,
            c.car_owner_name,
            c.status,
            t.last_entry_time,
            t.last_exit_time,
            t.B1_entry_time,
            t.B1_exit_time,
            t.B2_entry_time,
            t.B2_exit_time,
            t.daily_charge,
            t.penalty_charge,
            t.total_charge,
            t.remarks
        FROM db_tbl_11_cards_allocated c
        LEFT JOIN db_tbl_10_cards_master cm ON c.card_type = cm.card_type
        LEFT JOIN (
            SELECT 
                cardno,
                entry_time as last_entry_time,
                exit_time as last_exit_time,
                B1_entry_time,
                B1_exit_time,
                B2_entry_time,
                B2_exit_time,
                daily_charge,
                penalty_charge,
                total_charge,
                remarks,
                ROW_NUMBER() OVER (PARTITION BY cardno ORDER BY created_on DESC) as rn
            FROM db_tbl_21_TAG_log
        ) t ON c.cardno = t.cardno AND t.rn = 1
        WHERE c.card_type IS NOT NULL
        ORDER BY t.last_entry_time DESC";

            var data = conn.Query<TagUsageReportModel>(query).ToList();

            // Calculate days (needed for Excel column — unchanged logic)
            foreach (var item in data)
            {
                if (item.last_entry_time.HasValue)
                    item.days_since_last_use = (DateTime.Now - item.last_entry_time.Value).Days;
                else
                    item.days_since_last_use = null;
            }

            var excel = BuildTagUsageExcel(data);
            string filename = $"Tag_Usage_Report_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";

            return File(excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting cards usage report");
            return StatusCode(500, "Error exporting report");
        }
    }

    private byte[] BuildTagUsageExcel(List<TagUsageReportModel> data)
    {
        IWorkbook workbook = new XSSFWorkbook();
        ISheet sheet = workbook.CreateSheet("Cards Usage Report");

        var headers = new[] {
        "Card No", "Card Name", "Card Type", "Company ID", "Sub Company ID",
        "Sub Company Name", "Plate No", "Owner Name", "Status",
        "Last Entry Time", "Last Exit Time",
        "B1 Entry", "B1 Exit", "B2 Entry", "B2 Exit",
        "Daily Charge", "Penalty", "Total Charge", "Remarks", "Days Since Last Use"
    };

        // Header Style
        var headerStyle = workbook.CreateCellStyle();
        headerStyle.FillForegroundColor = IndexedColors.Grey40Percent.Index;
        headerStyle.FillPattern = FillPattern.SolidForeground;
        var headerFont = workbook.CreateFont();
        headerFont.IsBold = true;
        headerFont.Color = IndexedColors.White.Index;
        headerStyle.SetFont(headerFont);

        // Write header
        var headerRow = sheet.CreateRow(0);
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = headerRow.CreateCell(i);
            cell.SetCellValue(headers[i]);
            cell.CellStyle = headerStyle;
        }

        // Write data
        int rowNum = 1;
        foreach (var item in data)
        {
            var row = sheet.CreateRow(rowNum++);

            row.CreateCell(0).SetCellValue(item.cardno ?? "");
            row.CreateCell(1).SetCellValue(item.cardname ?? "");
            row.CreateCell(2).SetCellValue(item.card_type_remark ?? item.card_type ?? "");  // ✅ Show Remark
            row.CreateCell(3).SetCellValue(item.car_parkers_company_id ?? "");
            row.CreateCell(4).SetCellValue(item.car_parkers_sub_co_id ?? "");
            row.CreateCell(5).SetCellValue(item.car_parkers_sub_co_name ?? "");
            row.CreateCell(6).SetCellValue(item.car_plate_no ?? "");
            row.CreateCell(7).SetCellValue(item.car_owner_name ?? "");
            row.CreateCell(8).SetCellValue(item.status == "1" ? "Active" : "Inactive");
            row.CreateCell(9).SetCellValue(item.last_entry_time?.ToString("dd-MMM-yyyy HH:mm") ?? "Never");
            row.CreateCell(10).SetCellValue(item.last_exit_time?.ToString("dd-MMM-yyyy HH:mm") ?? "-");
            row.CreateCell(11).SetCellValue(item.B1_entry_time?.ToString("dd-MMM-yyyy HH:mm") ?? "-");
            row.CreateCell(12).SetCellValue(item.B1_exit_time?.ToString("dd-MMM-yyyy HH:mm") ?? "-");
            row.CreateCell(13).SetCellValue(item.B2_entry_time?.ToString("dd-MMM-yyyy HH:mm") ?? "-");
            row.CreateCell(14).SetCellValue(item.B2_exit_time?.ToString("dd-MMM-yyyy HH:mm") ?? "-");
            row.CreateCell(15).SetCellValue(item.daily_charge?.ToString() ?? "0");
            row.CreateCell(16).SetCellValue(item.penalty_charge?.ToString() ?? "0");
            row.CreateCell(17).SetCellValue(item.total_charge?.ToString() ?? "0");
            row.CreateCell(18).SetCellValue(item.remarks ?? "");

            var daysSince = item.last_entry_time.HasValue
                ? (DateTime.Now - item.last_entry_time.Value).Days.ToString()
                : "Never Used";
            row.CreateCell(19).SetCellValue(daysSince);
        }

        // Auto-size columns
        for (int i = 0; i < headers.Length; i++)
            sheet.AutoSizeColumn(i);

        using var ms = new MemoryStream();
        workbook.Write(ms);
        return ms.ToArray();
    }
    [HttpGet]
    public IActionResult CardTypeExitReport()
    {
        try
        {
            return View("~/Views/Reports/CardTypeExitReport.cshtml");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading card type exit report page");
            return StatusCode(500, "Error loading report page");
        }
    }

    [HttpPost]
    public IActionResult GetCardTypeExitReportData(DateTime? startDate, DateTime? endDate, string filterType = "today")
    {
        try
        {
            DateTime start, end;

            switch (filterType.ToLower())
            {
                case "today":
                    start = DateTime.Today;
                    end = DateTime.Today.AddDays(1).AddSeconds(-1);
                    break;
                case "yesterday":
                    start = DateTime.Today.AddDays(-1);
                    end = DateTime.Today.AddSeconds(-1);
                    break;
                case "daterange":
                    if (!startDate.HasValue || !endDate.HasValue)
                        return Json(new { success = false, message = "Please select valid date range" });
                    start = startDate.Value.Date;
                    end = endDate.Value.Date.AddDays(1).AddSeconds(-1);
                    break;
                default:
                    start = DateTime.Today;
                    end = DateTime.Today.AddDays(1).AddSeconds(-1);
                    break;
            }

            using var conn = new SqlConnection(GetDynamicConnectionString());

            var query = @"
        SELECT
            ISNULL(e.card_type, 'Unknown')                              AS CardType,
            ISNULL(cm.Remark, ISNULL(e.card_type, 'Unknown'))           AS CardTypeRemark,
            COUNT(e.id)                                                  AS TotalExits,
            ISNULL(SUM(CAST(e.charges AS DECIMAL(10,2))), 0)            AS TotalCharges,
            ISNULL(SUM(CAST(e.paid_amount AS DECIMAL(10,2))), 0)        AS TotalPaid,
            ISNULL(SUM(CAST(e.balance_to_collect AS DECIMAL(10,2))), 0) AS TotalPending,
            SUM(CASE WHEN ISNULL(CAST(e.paid_amount AS DECIMAL(10,2)), 0) > 0 THEN 1 ELSE 0 END) AS PaidCount,
            SUM(CASE WHEN ISNULL(CAST(e.paid_amount AS DECIMAL(10,2)), 0) = 0 THEN 1 ELSE 0 END) AS UnpaidCount
        FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited] e
        LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_10_cards_master] cm
            ON e.card_type = cm.card_type
        WHERE e.exit_time IS NOT NULL
          AND e.exit_time >= @StartDate AND e.exit_time <= @EndDate
        GROUP BY e.card_type, cm.Remark
        ORDER BY cm.Remark";

            var data = conn.Query<CardTypeExitReportModel>(query, new
            {
                StartDate = start,
                EndDate = end
            }).ToList();

            return Json(new
            {
                success = true,
                data = data,
                recordCount = data.Count,
                totalExits = data.Sum(x => x.TotalExits),
                totalCharged = data.Sum(x => x.TotalCharges),
                totalPaid = data.Sum(x => x.TotalPaid),
                totalPending = data.Sum(x => x.TotalPending),
                dateRange = $"{start:dd-MMM-yyyy} to {end:dd-MMM-yyyy}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading card type exit report data");
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public IActionResult ExportCardTypeExitReport(DateTime? startDate, DateTime? endDate, string filterType = "today")
    {
        try
        {
            DateTime start, end;

            switch (filterType.ToLower())
            {
                case "today":
                    start = DateTime.Today;
                    end = DateTime.Today.AddDays(1).AddSeconds(-1);
                    break;
                case "yesterday":
                    start = DateTime.Today.AddDays(-1);
                    end = DateTime.Today.AddSeconds(-1);
                    break;
                case "daterange":
                    if (!startDate.HasValue || !endDate.HasValue)
                        return BadRequest("Please select valid date range");
                    start = startDate.Value.Date;
                    end = endDate.Value.Date.AddDays(1).AddSeconds(-1);
                    break;
                default:
                    start = DateTime.Today;
                    end = DateTime.Today.AddDays(1).AddSeconds(-1);
                    break;
            }

            using var conn = new SqlConnection(GetDynamicConnectionString());

            var query = @"
        SELECT
            ISNULL(e.card_type, 'Unknown')                              AS CardType,
            ISNULL(cm.Remark, ISNULL(e.card_type, 'Unknown'))           AS CardTypeRemark,
            COUNT(e.id)                                                  AS TotalExits,
            ISNULL(SUM(CAST(e.charges AS DECIMAL(10,2))), 0)            AS TotalCharges,
            ISNULL(SUM(CAST(e.paid_amount AS DECIMAL(10,2))), 0)        AS TotalPaid,
            ISNULL(SUM(CAST(e.balance_to_collect AS DECIMAL(10,2))), 0) AS TotalPending,
            SUM(CASE WHEN ISNULL(CAST(e.paid_amount AS DECIMAL(10,2)), 0) > 0 THEN 1 ELSE 0 END) AS PaidCount,
            SUM(CASE WHEN ISNULL(CAST(e.paid_amount AS DECIMAL(10,2)), 0) = 0 THEN 1 ELSE 0 END) AS UnpaidCount
        FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited] e
        LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_10_cards_master] cm
            ON e.card_type = cm.card_type
        WHERE e.exit_time IS NOT NULL
          AND e.exit_time >= @StartDate AND e.exit_time <= @EndDate
        GROUP BY e.card_type, cm.Remark
        ORDER BY cm.Remark";

            var data = conn.Query<CardTypeExitReportModel>(query, new
            {
                StartDate = start,
                EndDate = end
            }).ToList();

            var excel = BuildCardTypeExitExcel(data, start, end);
            string filename = $"CardType_Exit_Report_{start:yyyyMMdd}_to_{end:yyyyMMdd}.xlsx";

            return File(excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting card type exit report");
            return StatusCode(500, "Error exporting report");
        }
    }

    private byte[] BuildCardTypeExitExcel(List<CardTypeExitReportModel> data, DateTime startDate, DateTime endDate)
    {
        IWorkbook workbook = new XSSFWorkbook();
        ISheet sheet = workbook.CreateSheet("Card Type Exit Report");

        // Title Row
        var titleRow = sheet.CreateRow(0);
        var titleCell = titleRow.CreateCell(0);
        titleCell.SetCellValue($"Card Type Exit Report - {startDate:dd-MMM-yyyy} to {endDate:dd-MMM-yyyy}");
        var titleStyle = workbook.CreateCellStyle();
        var titleFont = workbook.CreateFont();
        titleFont.IsBold = true;
        titleFont.FontHeightInPoints = 14;
        titleStyle.SetFont(titleFont);
        titleCell.CellStyle = titleStyle;
        sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(0, 0, 0, 6));

        // Empty row
        sheet.CreateRow(1);

        var headers = new[]
        {
        "Card Type", "Total Exits", "Paid Exits", "Unpaid Exits",
        "Total Charged", "Total Paid", "Pending Amount"
    };

        // Header Style
        var headerStyle = workbook.CreateCellStyle();
        headerStyle.FillForegroundColor = IndexedColors.Grey40Percent.Index;
        headerStyle.FillPattern = FillPattern.SolidForeground;
        headerStyle.Alignment = HorizontalAlignment.Center;
        headerStyle.VerticalAlignment = VerticalAlignment.Center;
        var headerFont = workbook.CreateFont();
        headerFont.IsBold = true;
        headerFont.Color = IndexedColors.White.Index;
        headerStyle.SetFont(headerFont);

        var headerRow = sheet.CreateRow(2);
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = headerRow.CreateCell(i);
            cell.SetCellValue(headers[i]);
            cell.CellStyle = headerStyle;
        }

        // Data Style
        var dataStyle = workbook.CreateCellStyle();
        dataStyle.Alignment = HorizontalAlignment.Left;
        dataStyle.VerticalAlignment = VerticalAlignment.Center;

        // Write data rows
        int rowNum = 3;
        foreach (var item in data)
        {
            var row = sheet.CreateRow(rowNum++);

            row.CreateCell(0).SetCellValue(item.CardTypeRemark ?? item.CardType ?? "-");
            row.CreateCell(1).SetCellValue(item.TotalExits);
            row.CreateCell(2).SetCellValue(item.PaidCount);
            row.CreateCell(3).SetCellValue(item.UnpaidCount);
            row.CreateCell(4).SetCellValue((double)item.TotalCharges);
            row.CreateCell(5).SetCellValue((double)item.TotalPaid);
            row.CreateCell(6).SetCellValue((double)item.TotalPending);

            for (int i = 0; i < 7; i++)
                row.GetCell(i).CellStyle = dataStyle;
        }

        // Grand Total Row
        var totalRowStyle = workbook.CreateCellStyle();
        var totalFont = workbook.CreateFont();
        totalFont.IsBold = true;
        totalRowStyle.SetFont(totalFont);
        totalRowStyle.FillForegroundColor = IndexedColors.LightYellow.Index;
        totalRowStyle.FillPattern = FillPattern.SolidForeground;

        var totalRow = sheet.CreateRow(rowNum + 1);
        var t0 = totalRow.CreateCell(0); t0.SetCellValue("Grand Total"); t0.CellStyle = totalRowStyle;
        var t1 = totalRow.CreateCell(1); t1.SetCellValue(data.Sum(x => x.TotalExits)); t1.CellStyle = totalRowStyle;
        var t2 = totalRow.CreateCell(2); t2.SetCellValue(data.Sum(x => x.PaidCount)); t2.CellStyle = totalRowStyle;
        var t3 = totalRow.CreateCell(3); t3.SetCellValue(data.Sum(x => x.UnpaidCount)); t3.CellStyle = totalRowStyle;
        var t4 = totalRow.CreateCell(4); t4.SetCellValue((double)data.Sum(x => x.TotalCharges)); t4.CellStyle = totalRowStyle;
        var t5 = totalRow.CreateCell(5); t5.SetCellValue((double)data.Sum(x => x.TotalPaid)); t5.CellStyle = totalRowStyle;
        var t6 = totalRow.CreateCell(6); t6.SetCellValue((double)data.Sum(x => x.TotalPending)); t6.CellStyle = totalRowStyle;

        // Auto-size columns
        for (int i = 0; i < headers.Length; i++)
        {
            sheet.AutoSizeColumn(i);
            sheet.SetColumnWidth(i, sheet.GetColumnWidth(i) + 1000);
        }

        using var ms = new MemoryStream();
        workbook.Write(ms);
        return ms.ToArray();
    }

    // ---- Model (add inside ReportsController or in Models/Reports folder) ----
    public class CardTypeExitReportModel
    {
        public string? CardType { get; set; }
        public string? CardTypeRemark { get; set; }   // ← ADD THIS
        public int TotalExits { get; set; }
        public decimal TotalCharges { get; set; }
        public decimal TotalPaid { get; set; }
        public decimal TotalPending { get; set; }
        public int PaidCount { get; set; }
        public int UnpaidCount { get; set; }
    }

    public class ManualGateReportModel
    {
        public int id { get; set; }
        public string? device { get; set; }
        public string? operation { get; set; }
        public string? remark { get; set; }
        public string? created_by { get; set; }
        public DateTime? created_on { get; set; }
    }
}

