using AmaanParkingSystem.Controllers;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public class PayStationCashLogController : BaseController
{
    private readonly ILogger<PayStationCashLogController> _logger;

    public PayStationCashLogController(ILogger<PayStationCashLogController> logger)
    {
        _logger = logger;
    }

    // ── GET: /PayStationCashLog ──────────────────────────────
    [HttpGet]
    public IActionResult Index()
    {
        try
        {
            return View("~/Views/Reports/PayStationCashLog.cshtml");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Pay Station Cash Log page");
            return StatusCode(500, "Error loading page");
        }
    }

    // ── POST: /PayStationCashLog/GetData ────────────────────
    [HttpPost]
    public IActionResult GetData(DateTime? startDate, DateTime? endDate, string filterType = "today")
    {
        try
        {
            var (start, end, error) = ResolveDate(filterType, startDate, endDate);
            if (error != null)
                return Json(new { success = false, message = error });

            using var conn = new SqlConnection(GetDynamicConnectionString());

            var query = @"
                SELECT
                    [id], [log_type], [pay_source], [created_on],
                    [card_name], [card_no], [entry_time], [parked_for],
                    [charged_kes], [payment_mode], [cash_paid_kes], [change_due_kes],
                    [notes_received], [recv_breakdown], [recv_total_kes],
                    [notes_dispensed], [change_breakdown], [change_total_kes],
                    [mpesa_ref], [checkout_id], [action_type], [validated_at],
                    [float_operation], [float_completed_at], [float_notes_loaded],
                    [float_denomination], [float_total_kes],
                    [before_float_cassette_levels], [after_float_cassette_levels],
                    [emptied_completed_at], [emptied_notes], [emptied_breakdown],
                    [emptied_total_kes], [emptied_note_count],
                    [FINAL_STACKED_NOTES], [TOTAL_STACKED_NOTES]
                FROM [AMAAN_PMS].[dbo].[db_tbl_26_pay_cash_log]
                WHERE [created_on] >= @StartDate AND [created_on] <= @EndDate
                ORDER BY [created_on] DESC";

            var data = conn.Query<PayStationCashLogModel>(query, new { StartDate = start, EndDate = end }).ToList();

            return Json(new
            {
                success = true,
                data,
                recordCount = data.Count,
                totalCharged = data.Sum(x => x.charged_kes ?? 0),
                totalCashPaid = data.Sum(x => x.cash_paid_kes ?? 0),
                totalChangeDue = data.Sum(x => x.change_due_kes ?? 0),
                totalRecv = data.Sum(x => x.recv_total_kes ?? 0),
                totalChange = data.Sum(x => x.change_total_kes ?? 0),
                totalFloat = data.Sum(x => x.float_total_kes ?? 0),
                totalEmptied = data.Sum(x => x.emptied_total_kes ?? 0),
                dateRange = $"{start:dd-MMM-yyyy} to {end:dd-MMM-yyyy}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Pay Station Cash Log data");
            return Json(new { success = false, message = ex.Message });
        }
    }

    // ── GET: /PayStationCashLog/Export ──────────────────────
    [HttpGet]
    public IActionResult Export(DateTime? startDate, DateTime? endDate, string filterType = "today")
    {
        try
        {
            var (start, end, error) = ResolveDate(filterType, startDate, endDate);
            if (error != null)
                return BadRequest(error);

            using var conn = new SqlConnection(GetDynamicConnectionString());

            var query = @"
                SELECT
                    [id], [log_type], [pay_source], [created_on],
                    [card_name], [card_no], [entry_time], [parked_for],
                    [charged_kes], [payment_mode], [cash_paid_kes], [change_due_kes],
                    [notes_received], [recv_breakdown], [recv_total_kes],
                    [notes_dispensed], [change_breakdown], [change_total_kes],
                    [mpesa_ref], [checkout_id], [action_type], [validated_at],
                    [float_operation], [float_completed_at], [float_notes_loaded],
                    [float_denomination], [float_total_kes],
                    [before_float_cassette_levels], [after_float_cassette_levels],
                    [emptied_completed_at], [emptied_notes], [emptied_breakdown],
                    [emptied_total_kes], [emptied_note_count],
                    [FINAL_STACKED_NOTES], [TOTAL_STACKED_NOTES]
                FROM [AMAAN_PMS].[dbo].[db_tbl_26_pay_cash_log]
                WHERE [created_on] >= @StartDate AND [created_on] <= @EndDate
                ORDER BY [created_on] DESC";

            var data = conn.Query<PayStationCashLogModel>(query, new { StartDate = start, EndDate = end }).ToList();

            var excel = BuildExcel(data, start, end);
            var filename = $"PayStation_CashLog_{start:yyyyMMdd}_to_{end:yyyyMMdd}.xlsx";
            return File(excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting Pay Station Cash Log");
            return StatusCode(500, "Error exporting report");
        }
    }

    // ── Date helper ─────────────────────────────────────────
    private (DateTime start, DateTime end, string? error) ResolveDate(
        string filterType, DateTime? startDate, DateTime? endDate)
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
                int diff = (int)DateTime.Today.DayOfWeek - (int)DayOfWeek.Monday;
                if (diff < 0) diff += 7;
                start = DateTime.Today.AddDays(-diff - 7);
                end = start.AddDays(7).AddSeconds(-1);
                break;
            case "lastmonth":
                var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                start = first.AddMonths(-1);
                end = first.AddSeconds(-1);
                break;
            case "thismonth":
                start = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                end = DateTime.Today.AddDays(1).AddSeconds(-1);
                break;
            case "daterange":
                if (!startDate.HasValue || !endDate.HasValue)
                    return (default, default, "Please select a valid date range");
                start = startDate.Value.Date;
                end = endDate.Value.Date.AddDays(1).AddSeconds(-1);
                break;
            default:
                start = DateTime.Today;
                end = DateTime.Today.AddDays(1).AddSeconds(-1);
                break;
        }
        return (start, end, null);
    }

    // ── Excel builder ────────────────────────────────────────
    private byte[] BuildExcel(List<PayStationCashLogModel> data, DateTime startDate, DateTime endDate)
    {
        IWorkbook workbook = new XSSFWorkbook();
        ISheet sheet = workbook.CreateSheet("Pay Station Cash Log");

        var titleRow = sheet.CreateRow(0);
        var titleCell = titleRow.CreateCell(0);
        titleCell.SetCellValue($"Pay Station Cash Log - {startDate:dd-MMM-yyyy} to {endDate:dd-MMM-yyyy}");
        var titleStyle = workbook.CreateCellStyle();
        var titleFont = workbook.CreateFont();
        titleFont.IsBold = true;
        titleFont.FontHeightInPoints = 13;
        titleStyle.SetFont(titleFont);
        titleCell.CellStyle = titleStyle;
        sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(0, 0, 0, 35));
        sheet.CreateRow(1);

        var headers = new[]
        {
        "ID","Log Type","Pay Source","Created On","Card Name","Card No",
        "Entry Time","Parked For","Charged (KES)","Payment Mode",
        "Cash Paid (KES)","Change Due (KES)","Notes Received","Recv Breakdown",
        "Recv Total (KES)","Notes Dispensed","Change Breakdown","Change Total (KES)",
        "M-Pesa Ref","Checkout ID","Action Type","Validated At",
        "Float Op","Float Completed","Float Notes Loaded","Float Denom","Float Total (KES)",
        "Before Float Cassette","After Float Cassette",
        "Emptied Completed","Emptied Notes","Emptied Breakdown",
        "Emptied Total (KES)","Emptied Note Count","Final Stacked Notes","Total Stacked Notes"
    };

        var hStyle = workbook.CreateCellStyle();
        hStyle.FillForegroundColor = IndexedColors.Grey40Percent.Index;
        hStyle.FillPattern = FillPattern.SolidForeground;
        hStyle.Alignment = HorizontalAlignment.Center;
        hStyle.VerticalAlignment = VerticalAlignment.Center;
        var hFont = workbook.CreateFont();
        hFont.IsBold = true;
        hFont.Color = IndexedColors.White.Index;
        hStyle.SetFont(hFont);

        var headerRow = sheet.CreateRow(2);
        for (int i = 0; i < headers.Length; i++)
        {
            var c = headerRow.CreateCell(i);
            c.SetCellValue(headers[i]);
            c.CellStyle = hStyle;
        }

        var dStyle = workbook.CreateCellStyle();
        dStyle.Alignment = HorizontalAlignment.Left;
        dStyle.VerticalAlignment = VerticalAlignment.Center;

        var dtStyle = workbook.CreateCellStyle();
        var dtFormat = workbook.CreateDataFormat();
        dtStyle.DataFormat = dtFormat.GetFormat("dd-mmm-yyyy hh:mm:ss");
        dtStyle.Alignment = HorizontalAlignment.Left;
        dtStyle.VerticalAlignment = VerticalAlignment.Center;

        int row = 3;
        foreach (var item in data)
        {
            var r = sheet.CreateRow(row++);

            void S(int col, string? val)
            {
                var cell = r.CreateCell(col);
                cell.SetCellValue(val ?? "");
                cell.CellStyle = dStyle;
            }

            void N(int col, decimal? val)
            {
                var cell = r.CreateCell(col);
                cell.SetCellValue((double)(val ?? 0));
                cell.CellStyle = dStyle;
            }

            void D(int col, DateTime? val)
            {
                var cell = r.CreateCell(col);
                if (val.HasValue)
                {
                    cell.SetCellValue(val.Value);
                    cell.CellStyle = dtStyle;
                }
                else
                {
                    cell.SetCellValue("-");
                    cell.CellStyle = dStyle;
                }
            }

            r.CreateCell(0).SetCellValue(item.id); r.GetCell(0).CellStyle = dStyle;
            S(1, item.log_type);
            S(2, item.pay_source);
            D(3, item.created_on);
            S(4, item.card_name);
            S(5, item.card_no);
            D(6, item.entry_time);
            S(7, item.parked_for);
            N(8, item.charged_kes);
            S(9, item.payment_mode);
            N(10, item.cash_paid_kes);
            N(11, item.change_due_kes);
            S(12, item.notes_received);
            S(13, item.recv_breakdown);
            N(14, item.recv_total_kes);
            S(15, item.notes_dispensed);
            S(16, item.change_breakdown);
            N(17, item.change_total_kes);
            S(18, item.mpesa_ref);
            S(19, item.checkout_id);
            S(20, item.action_type);
            D(21, item.validated_at);
            S(22, item.float_operation);
            D(23, item.float_completed_at);
            S(24, item.float_notes_loaded);
            S(25, item.float_denomination);
            N(26, item.float_total_kes);
            S(27, item.before_float_cassette_levels);
            S(28, item.after_float_cassette_levels);
            D(29, item.emptied_completed_at);
            S(30, item.emptied_notes);
            S(31, item.emptied_breakdown);
            N(32, item.emptied_total_kes);
            var cell33 = r.CreateCell(33);
            cell33.SetCellValue(item.emptied_note_count ?? 0);
            cell33.CellStyle = dStyle;
            S(34, item.FINAL_STACKED_NOTES);
            S(35, item.TOTAL_STACKED_NOTES);
        }

        var totStyle = workbook.CreateCellStyle();
        var totFont = workbook.CreateFont();
        totFont.IsBold = true;
        totStyle.SetFont(totFont);
        totStyle.FillForegroundColor = IndexedColors.LightYellow.Index;
        totStyle.FillPattern = FillPattern.SolidForeground;

        var totRow = sheet.CreateRow(row + 1);
        var t0 = totRow.CreateCell(0); t0.SetCellValue($"Total - {data.Count} records"); t0.CellStyle = totStyle;
        var t8 = totRow.CreateCell(8); t8.SetCellValue((double)data.Sum(x => x.charged_kes ?? 0)); t8.CellStyle = totStyle;
        var t10 = totRow.CreateCell(10); t10.SetCellValue((double)data.Sum(x => x.cash_paid_kes ?? 0)); t10.CellStyle = totStyle;
        var t11 = totRow.CreateCell(11); t11.SetCellValue((double)data.Sum(x => x.change_due_kes ?? 0)); t11.CellStyle = totStyle;
        var t14 = totRow.CreateCell(14); t14.SetCellValue((double)data.Sum(x => x.recv_total_kes ?? 0)); t14.CellStyle = totStyle;
        var t17 = totRow.CreateCell(17); t17.SetCellValue((double)data.Sum(x => x.change_total_kes ?? 0)); t17.CellStyle = totStyle;
        var t26 = totRow.CreateCell(26); t26.SetCellValue((double)data.Sum(x => x.float_total_kes ?? 0)); t26.CellStyle = totStyle;
        var t32 = totRow.CreateCell(32); t32.SetCellValue((double)data.Sum(x => x.emptied_total_kes ?? 0)); t32.CellStyle = totStyle;

        // IMPORTANT FIX: cap width to Excel/NPOI max allowed width
        const int maxExcelWidth = 255 * 256;
        for (int i = 0; i < headers.Length; i++)
        {
            sheet.AutoSizeColumn(i);
            int width = (int)sheet.GetColumnWidth(i) + 800;
            if (width > maxExcelWidth)
                width = maxExcelWidth;

            sheet.SetColumnWidth(i, width);
        }

        using var ms = new MemoryStream();
        workbook.Write(ms);
        return ms.ToArray();
    }
}

    // ── Model (kept in same file) ────────────────────────────────
    public class PayStationCashLogModel
{
    public long id { get; set; }
    public string? log_type { get; set; }
    public string? pay_source { get; set; }
    public DateTime? created_on { get; set; }
    public string? card_name { get; set; }
    public string? card_no { get; set; }
    public DateTime? entry_time { get; set; }
    public string? parked_for { get; set; }
    public decimal? charged_kes { get; set; }
    public string? payment_mode { get; set; }
    public decimal? cash_paid_kes { get; set; }
    public decimal? change_due_kes { get; set; }
    public string? notes_received { get; set; }
    public string? recv_breakdown { get; set; }
    public decimal? recv_total_kes { get; set; }
    public string? notes_dispensed { get; set; }
    public string? change_breakdown { get; set; }
    public decimal? change_total_kes { get; set; }
    public string? mpesa_ref { get; set; }
    public string? checkout_id { get; set; }
    public string? action_type { get; set; }
    public DateTime? validated_at { get; set; }
    public string? float_operation { get; set; }
    public DateTime? float_completed_at { get; set; }
    public string? float_notes_loaded { get; set; }
    public string? float_denomination { get; set; }
    public decimal? float_total_kes { get; set; }
    public string? before_float_cassette_levels { get; set; }
    public string? after_float_cassette_levels { get; set; }
    public DateTime? emptied_completed_at { get; set; }
    public string? emptied_notes { get; set; }
    public string? emptied_breakdown { get; set; }
    public decimal? emptied_total_kes { get; set; }
    public int? emptied_note_count { get; set; }
    public string? FINAL_STACKED_NOTES { get; set; }
    public string? TOTAL_STACKED_NOTES { get; set; }
}