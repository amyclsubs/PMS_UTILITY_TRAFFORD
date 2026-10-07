using Microsoft.AspNetCore.Mvc;
using AmaanParkingSystem.Services;
using ClosedXML.Excel;
using System.IO;

namespace AmaanParkingSystem.Controllers
{
    public class FocReportController : BaseController
    {
        private readonly FocReportService _focReportService;

        public FocReportController(FocReportService focReportService)
        {
            _focReportService = focReportService;
        }

        // View path unchanged
        public IActionResult Index()
        {
            return View("~/Views/Reports/FocReport.cshtml");
        }

        // New: get distinct payment sources for dropdown
        [HttpGet]
        public async Task<IActionResult> GetPaymentSources()
        {
            try
            {
                var sources = await _focReportService.GetDistinctPaymentSources();
                return Json(new { success = true, data = sources });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> GetData([FromBody] DateFilterRequest request)
        {
            try
            {
                var data = await _focReportService.GetFocReportData(
                    request.StartDate,
                    request.EndDate,
                    request.PaymentSources);

                return Json(new { success = true, data = data });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportExcel(string startDate, string endDate, string? paymentSources)
        {
            DateTime start = DateTime.Parse(startDate);
            DateTime end = DateTime.Parse(endDate);

            List<string>? sources = null;
            if (!string.IsNullOrWhiteSpace(paymentSources))
            {
                sources = paymentSources
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }

            var data = await _focReportService.GetFocReportData(start, end, sources);

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("FOC Report");

            string[] headers = {
                "Trans ID", "Card Name", "Entry Time", "Type", "Source",
                "Val. Time", "Val. Charges", "Val. Paymode",
                "Reval. Time", "Reval. Charges", "Reason", "Remark"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cell(1, i + 1).Value = headers[i];
                worksheet.Cell(1, i + 1).Style.Font.Bold = true;
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
                worksheet.Cell(row, 11).SetValue(item.revalidation_paymode?.ToString() ?? "");

                worksheet.Cell(row, 11).SetValue(item.reason?.ToString() ?? "");
                worksheet.Cell(row, 12).SetValue(item.remark?.ToString() ?? "");
                row++;
            }

            worksheet.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var fileName = $"FocReport_{start:ddMMMyyyy}_to_{end:ddMMMyyyy}.xlsx";
            return File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }
    }

    public class DateFilterRequest
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public List<string>? PaymentSources { get; set; }
    }
}
