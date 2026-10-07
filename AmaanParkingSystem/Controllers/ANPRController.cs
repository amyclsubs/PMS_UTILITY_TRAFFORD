using Microsoft.AspNetCore.Mvc;
using AmaanParkingSystem.Services;
using AmaanParkingSystem.Models.ANPR;
using ClosedXML.Excel;

namespace AmaanParkingSystem.Controllers
{
    public class ANPRController : BaseController
    {
        private readonly ANPRService _svc;
        private readonly ILogger<ANPRController> _logger;

        public ANPRController(ANPRService svc, ILogger<ANPRController> logger)
        {
            _svc = svc;
            _logger = logger;
        }

        public IActionResult Reports() => View();

        [HttpPost]
        public async Task<IActionResult> GetReports([FromBody] ANPRFilterRequest req)
        {
            try
            {
                var (stats, rows) = await _svc.GetReport(req.StartDate.Date, req.EndDate.Date.AddDays(1));
                return Json(new { success = true, stats, rows });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ANPR GetReports failed");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Today()
        {
            try { var (s, r) = await _svc.GetToday(); return Json(new { success = true, stats = s, rows = r }); }
            catch (Exception ex) { _logger.LogError(ex, "ANPR Today failed"); return Json(new { success = false, message = ex.Message }); }
        }

        [HttpGet]
        public async Task<IActionResult> Yesterday()
        {
            try { var (s, r) = await _svc.GetYesterday(); return Json(new { success = true, stats = s, rows = r }); }
            catch (Exception ex) { _logger.LogError(ex, "ANPR Yesterday failed"); return Json(new { success = false, message = ex.Message }); }
        }

        [HttpGet]
        public async Task<IActionResult> ThisWeek()
        {
            try { var (s, r) = await _svc.GetThisWeek(); return Json(new { success = true, stats = s, rows = r }); }
            catch (Exception ex) { _logger.LogError(ex, "ANPR ThisWeek failed"); return Json(new { success = false, message = ex.Message }); }
        }

        [HttpGet]
        public async Task<IActionResult> ThisMonth()
        {
            try { var (s, r) = await _svc.GetThisMonth(); return Json(new { success = true, stats = s, rows = r }); }
            catch (Exception ex) { _logger.LogError(ex, "ANPR ThisMonth failed"); return Json(new { success = false, message = ex.Message }); }
        }

        [HttpGet]
        public async Task<IActionResult> ExportExcel(DateTime from, DateTime to)
        {
            try
            {
                var dt = await _svc.ExportToDataTable(from.Date, to.Date.AddDays(1));
                using var wb = new XLWorkbook();
                var ws = wb.Worksheets.Add("ANPR Reports");
                if (dt.Rows.Count > 0)
                {
                    var t = ws.Cell(1, 1).InsertTable(dt);
                    t.Theme = XLTableTheme.TableStyleMedium2;
                }
                ws.Columns().AdjustToContents();
                using var ms = new MemoryStream();
                wb.SaveAs(ms);
                return File(ms.ToArray(),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"ANPR_Report_{from:yyyyMMdd}_{to:yyyyMMdd}.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ANPR ExportExcel failed");
                return BadRequest(ex.Message);
            }
        }
    }
}
