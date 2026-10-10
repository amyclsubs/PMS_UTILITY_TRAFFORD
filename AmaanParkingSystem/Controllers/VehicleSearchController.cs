using ClosedXML.Excel;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Text;

namespace AmaanParkingSystem.Controllers
{
    /// <summary>
    /// Vehicle Search — one search box that looks for a car (plate no / card no / card name /
    /// transaction id) across the Exit, Transactions, Tag Log (and optionally Entered) tables.
    /// Read-only: every role that can log in may use it. Table / column names are a fixed
    /// whitelist (never taken from the request); all user input is passed as SQL parameters.
    /// </summary>
    public class VehicleSearchController : BaseController
    {
        private readonly ILogger<VehicleSearchController> _logger;

        public VehicleSearchController(ILogger<VehicleSearchController> logger)
        {
            _logger = logger;
        }

        // ── Table whitelist ────────────────────────────────────────────────────
        private sealed record SearchTable(
            string Key,
            string Label,
            string Table,
            string DateColumn,
            string[] PlateColumns,
            string[] OtherColumns,
            string? SelectList = null);

        private static readonly SearchTable[] Tables =
{
            new("exit",    "Exit (Car Exited)", "dbo.db_tbl_15_car_exited",  "exit_time",  new[] { "car_plate_no" },
                new[] { "cardno", "card_name", "transaction_id" }),
            new("trans",   "Transactions",      "dbo.db_tbl_19_transactions", "created_on", new[] { "car_plate_no" },
                new[] { "cardno", "cardname", "transaction_id" }),
            new("taglog",  "Tag Log",           "dbo.db_tbl_21_TAG_log",      "created_on", new[] { "car_plate_no" },
                new[] { "cardno", "card_name" }),
            new("anpr",    "ANPR Dump",         "dbo.db_tbl_27_ANPR_dump",    "CaptureDateTime",
                new[] { "PlateNumber", "ModifiedPlate" },
                new string[0],
                // image blobs (LargeImageData, CropImageData) deliberately left out
                "[Id],[CaptureDate],[CaptureTime],[CaptureDateTime],[DeviceType],[PlateNumber],[ModifiedPlate]," +
                "[PlateType],[Confidence],[IsCorrect],[IsModified],[ManualEntered],[CreatedAt],[UpdatedAt]," +
                "[LargeImagePath],[SmallImagePath],[CroppedPlatePath]"),
            new("entered", "Entered (Inside Now)", "dbo.db_tbl_14_car_enterd", "entry_time", new[] { "car_plate_no" },
                new[] { "cardno", "card_name" }),
            new("notexited", "Removed / Not Exited", "dbo.db_tbl_27_Cards_notexited", "removed_at", new[] { "car_plate_no" },
                new[] { "cardno", "card_name", "mpesa_receipt_number" }),
        };

        public class VehicleSearchRequest
        {
            public string? Query { get; set; }
            /// <summary>contains | exact | starts</summary>
            public string? Mode { get; set; } = "contains";
            public List<string>? Tables { get; set; }
            public DateTime? FromDate { get; set; }
            public DateTime? ToDate { get; set; }
            public int? Limit { get; set; }
        }

        [HttpGet]
        public IActionResult Index()
        {
            ViewBag.Tables = Tables.Select(t => new KeyValuePair<string, string>(t.Key, t.Label)).ToList();
            return View();
        }

        // ── Search (AJAX) ──────────────────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Search([FromBody] VehicleSearchRequest req)
        {
            try
            {
                var term = (req?.Query ?? "").Trim();
                if (term.Length < 2)
                    return Json(new { success = false, message = "Please enter at least 2 characters." });

                int limit = Math.Clamp(req!.Limit ?? 500, 1, 5000);
                var selected = ResolveTables(req.Tables);
                if (selected.Count == 0)
                    return Json(new { success = false, message = "Select at least one table to search." });

                var results = new List<object>();
                await using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                foreach (var t in selected)
                {
                    results.Add(await RunOne(conn, t, term, req, limit));
                }

                return Json(new { success = true, query = term, results });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vehicle search failed");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ── Excel export (all selected tables, one sheet each) ────────────────
        [HttpGet]
        public async Task<IActionResult> Export(string? q, string? mode, string? tables,
                                                DateTime? from, DateTime? to)
        {
            try
            {
                var term = (q ?? "").Trim();
                if (term.Length < 2) return BadRequest("Enter at least 2 characters.");

                var req = new VehicleSearchRequest { Query = term, Mode = mode, FromDate = from, ToDate = to };
                var selected = ResolveTables(tables?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());

                using var wb = new XLWorkbook();
                await using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                foreach (var t in selected)
                {
                    var r = await RunOne(conn, t, term, req, 50000);
                    var ws = wb.Worksheets.Add(t.Key);
                    var cols = r.Columns;
                    for (int c = 0; c < cols.Count; c++)
                    {
                        var h = ws.Cell(1, c + 1);
                        h.Value = cols[c];
                        h.Style.Font.Bold = true;
                        h.Style.Fill.BackgroundColor = XLColor.FromHtml("#3b7dd6");
                        h.Style.Font.FontColor = XLColor.White;
                    }
                    for (int i = 0; i < r.Rows.Count; i++)
                        for (int c = 0; c < cols.Count; c++)
                        {
                            var v = r.Rows[i][cols[c]];
                            var cell = ws.Cell(i + 2, c + 1);
                            switch (v)
                            {
                                case null: break;
                                case DateTime d: cell.Value = d; cell.Style.DateFormat.Format = "yyyy-MM-dd HH:mm:ss"; break;
                                case decimal m: cell.Value = m; break;
                                case int n: cell.Value = n; break;
                                case long l: cell.Value = l; break;
                                case double db: cell.Value = db; break;
                                default: cell.Value = v.ToString(); break;
                            }
                        }
                    ws.SheetView.FreezeRows(1);
                    if (cols.Count > 0) ws.Columns().AdjustToContents(1, Math.Min(r.Rows.Count + 1, 200));
                }

                if (!wb.Worksheets.Any()) return BadRequest("No tables selected.");

                using var ms = new MemoryStream();
                wb.SaveAs(ms);
                return File(ms.ToArray(),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"Vehicle_Search_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vehicle search export failed");
                return StatusCode(500, "Error exporting search results: " + ex.Message);
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────
        private static List<SearchTable> ResolveTables(List<string>? keys)
        {
            if (keys == null || keys.Count == 0)
                return Tables.Where(t => t.Key != "entered").ToList(); // default: exit, transactions, taglog
            return Tables.Where(t => keys.Contains(t.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        private sealed class TableResult
        {
            public string Key { get; set; } = "";
            public string Label { get; set; } = "";
            public int Total { get; set; }
            public bool Truncated { get; set; }
            public string? Error { get; set; }
            public List<string> Columns { get; set; } = new();
            public List<Dictionary<string, object?>> Rows { get; set; } = new();
        }

        private async Task<TableResult> RunOne(SqlConnection conn, SearchTable t, string term,
                                               VehicleSearchRequest req, int limit)
        {
            var res = new TableResult { Key = t.Key, Label = t.Label };
            try
            {
                string mode = (req.Mode ?? "contains").ToLowerInvariant();
                string esc = EscapeLike(term);
                string escNorm = EscapeLike(NormalizePlate(term));

                string pattern, patternNorm;
                switch (mode)
                {
                    case "exact":  pattern = esc;        patternNorm = escNorm;        break;
                    case "starts": pattern = esc + "%";  patternNorm = escNorm + "%";  break;
                    default:       pattern = "%" + esc + "%"; patternNorm = "%" + escNorm + "%"; break;
                }

                // Plate column: compare with spaces / hyphens / dots stripped on both sides,
                // so "KAB 123C", "KAB-123C" and "kab123c" all match.
                var conds = new List<string>();
                foreach (var pc in t.PlateColumns)
                    conds.Add($"REPLACE(REPLACE(REPLACE([{pc}],' ',''),'-',''),'.','') LIKE @pn ESCAPE '\\'");
                foreach (var c in t.OtherColumns)
                    conds.Add($"[{c}] LIKE @p ESCAPE '\\'");

                var where = new StringBuilder("(" + string.Join(" OR ", conds) + ")");
                var prm = new DynamicParameters();
                prm.Add("p", pattern);
                prm.Add("pn", patternNorm);

                if (req.FromDate.HasValue)
                {
                    where.Append($" AND [{t.DateColumn}] >= @from");
                    prm.Add("from", req.FromDate.Value.Date);
                }
                if (req.ToDate.HasValue)
                {
                    where.Append($" AND [{t.DateColumn}] < @to");
                    prm.Add("to", req.ToDate.Value.Date.AddDays(1));
                }

                var countSql = $"SELECT COUNT(*) FROM {t.Table} WHERE {where}";
                var dataSql = $"SELECT TOP (@limit) {t.SelectList ?? "*"} FROM {t.Table} WHERE {where} ORDER BY [{t.DateColumn}] DESC";
                prm.Add("limit", limit);

                res.Total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, prm, commandTimeout: 120));
                var rows = await conn.QueryAsync(new CommandDefinition(dataSql, prm, commandTimeout: 120));

                foreach (var row in rows)
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var kv in (IDictionary<string, object>)row)
                    {
                        dict[kv.Key] = kv.Value switch
                        {
                            null => null,
                            DBNull => null,
                            byte[] => "(binary)",
                            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
                            _ => kv.Value
                        };
                    }
                    if (res.Columns.Count == 0) res.Columns = dict.Keys.ToList();
                    res.Rows.Add(dict);
                }
                res.Truncated = res.Total > res.Rows.Count;
            }
            catch (Exception ex)
            {
                // One table failing (missing table / column on a given site) must not hide the others.
                _logger.LogWarning(ex, "Vehicle search failed on {Table}", t.Table);
                res.Error = ex.Message;
            }
            return res;
        }

        private static string NormalizePlate(string s) =>
            s.Replace(" ", "").Replace("-", "").Replace(".", "");

        private static string EscapeLike(string s) =>
            s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
    }
}
