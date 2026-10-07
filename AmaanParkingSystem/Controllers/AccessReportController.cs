using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using NPOI.SS.UserModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Controllers
{
    public class AccessReportController : BaseController
    {
        private readonly ILogger<AccessReportController> _logger;

        // ── Hardcoded card types — matches db_tbl_10_cards_master order ──────
        private static readonly string[] CardTypes = new[]
        {
            "Temporary", "SEASONAL", "VIP", "DAILY_ACCESS", "CASH_CARD"
        };

        public AccessReportController(ILogger<AccessReportController> logger)
        {
            _logger = logger;
        }

        // =====================================================================
        // GET — Views
        // =====================================================================
        //TESTING
        [HttpGet]
        public IActionResult DailyEntryExit()
            => View("~/Views/Reports/DailyEntryExit.cshtml");

        [HttpGet]
        public IActionResult MonthlyEntryExit()
            => View("~/Views/Reports/MonthlyEntryExit.cshtml");

        [HttpGet]
        public IActionResult YearlyEntryExit()
            => View("~/Views/Reports/YearlyEntryExit.cshtml");

        [HttpGet]
        public IActionResult Occupancy()
            => View("~/Views/Reports/Occupancy.cshtml");
        [HttpGet]
        public IActionResult BayUtilization()
        {
            return View("~/Views/Reports/BayUtilization.cshtml");
        }

        // =====================================================================
        // DAILY ENTRY & EXIT
        // Entries = cars whose entry_time falls on the selected date
        //           from BOTH tables (still-inside + already-exited)
        // Exits   = cars whose exit_time falls on the selected date
        //           only from db_tbl_15_car_exited
        // Grouped hourly: 00:00-01:00 ... 23:00-24:00
        // =====================================================================
        [HttpPost]
        public async Task<IActionResult> GetDailyEntryExit(DateTime? selectedDate)
        {
            try
            {
                var start = (selectedDate?.Date) ?? DateTime.Today;
                var end = start.AddDays(1);

                const string sql = @"
WITH hrs AS (
    SELECT TOP 24 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS hr
    FROM sys.all_columns
),
card_types AS (
    SELECT card_type FROM dbo.db_tbl_10_cards_master
),
spine AS (
    SELECT h.hr, c.card_type
    FROM hrs h CROSS JOIN card_types c
),
-- Entries from active table (still inside)
ea AS (
    SELECT card_type, DATEPART(HOUR, entry_time) AS hr, COUNT(*) AS cnt
    FROM dbo.db_tbl_14_car_enterd
    WHERE entry_time >= @start AND entry_time < @end
    GROUP BY card_type, DATEPART(HOUR, entry_time)
),
-- Entries from exited table (entered today, already left)
ee AS (
    SELECT card_type, DATEPART(HOUR, entry_time) AS hr, COUNT(*) AS cnt
    FROM dbo.db_tbl_15_car_exited
    WHERE entry_time >= @start AND entry_time < @end
    GROUP BY card_type, DATEPART(HOUR, entry_time)
),
-- Exits today
ex AS (
    SELECT card_type, DATEPART(HOUR, exit_time) AS hr, COUNT(*) AS cnt
    FROM dbo.db_tbl_15_car_exited
    WHERE exit_time >= @start AND exit_time < @end
    GROUP BY card_type, DATEPART(HOUR, exit_time)
),
-- REMAIN: cars physically inside at the END of each hour
-- = (active table: entered in or before this hour, still inside)
-- + (exited table: entered in or before this hour, exited AFTER this hour ended)
rm AS (
    -- Still inside (active table) — entered in this hour
    SELECT card_type, DATEPART(HOUR, entry_time) AS hr, COUNT(*) AS cnt
    FROM dbo.db_tbl_14_car_enterd
    WHERE entry_time >= @start AND entry_time < @end
    GROUP BY card_type, DATEPART(HOUR, entry_time)

    UNION ALL

    -- Entered today this hour, exited tomorrow or later
    SELECT card_type, DATEPART(HOUR, entry_time) AS hr, COUNT(*) AS cnt
    FROM dbo.db_tbl_15_car_exited
    WHERE entry_time >= @start AND entry_time < @end
      AND exit_time  >= @end
    GROUP BY card_type, DATEPART(HOUR, entry_time)
)
SELECT
    s.hr,
    RIGHT('0'+CAST(s.hr   AS VARCHAR(2)),2)+':00 - '+
    RIGHT('0'+CAST(s.hr+1 AS VARCHAR(2)),2)+':00'   AS hour_label,
    s.card_type,
    ISNULL(ea.cnt, 0) + ISNULL(ee.cnt, 0)          AS total_in,
    ISNULL(ex.cnt, 0)                               AS total_out,
    ISNULL(rm.cnt, 0)                               AS total_remain
FROM spine s
LEFT JOIN ea ON ea.card_type = s.card_type AND ea.hr = s.hr
LEFT JOIN ee ON ee.card_type = s.card_type AND ee.hr = s.hr
LEFT JOIN ex ON ex.card_type = s.card_type AND ex.hr = s.hr
LEFT JOIN (
    SELECT card_type, hr, SUM(cnt) AS cnt
    FROM rm
    GROUP BY card_type, hr
) rm ON rm.card_type = s.card_type AND rm.hr = s.hr
ORDER BY s.hr, s.card_type;";

                using var conn = new SqlConnection(GetDynamicConnectionString());
                var flat = await conn.QueryAsync(sql, new { start, end });
                var pivoted = PivotToRows(flat,
                                keySelector: r => (int)r.hr,
                                labelSelector: r => (string)r.hour_label);

                return Json(new
                {
                    success = true,
                    cardTypes = CardTypes,
                    rows = pivoted.rows,
                    totals = pivoted.totals,
                    averages = pivoted.averages
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDailyEntryExit failed");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // =====================================================================
        // MONTHLY ENTRY & EXIT
        // Grouped by calendar day within the selected month/year
        // =====================================================================
        [HttpPost]
        public async Task<IActionResult> GetMonthlyEntryExit(int month, int year)
        {
            try
            {
                var start = new DateTime(year, month, 1);
                var end = start.AddMonths(1);
                int days = DateTime.DaysInMonth(year, month);

                const string sql = @"
WITH day_nums AS (
    SELECT TOP (@days) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS d
    FROM sys.all_columns
),
card_types AS (
    SELECT card_type FROM dbo.db_tbl_10_cards_master
),
spine AS (
    SELECT DATEADD(DAY, dn.d - 1, @start) AS day_date,
           dn.d,
           c.card_type
    FROM day_nums dn CROSS JOIN card_types c
),
ea AS (
    SELECT card_type, DATEDIFF(DAY, @start, entry_time) + 1 AS d, COUNT(*) AS cnt
    FROM dbo.db_tbl_14_car_enterd
    WHERE entry_time >= @start AND entry_time < @end
    GROUP BY card_type, DATEDIFF(DAY, @start, entry_time)
),
ee AS (
    SELECT card_type, DATEDIFF(DAY, @start, entry_time) + 1 AS d, COUNT(*) AS cnt
    FROM dbo.db_tbl_15_car_exited
    WHERE entry_time >= @start AND entry_time < @end
    GROUP BY card_type, DATEDIFF(DAY, @start, entry_time)
),
ex AS (
    SELECT card_type, DATEDIFF(DAY, @start, exit_time) + 1 AS d, COUNT(*) AS cnt
    FROM dbo.db_tbl_15_car_exited
    WHERE exit_time >= @start AND exit_time < @end
    GROUP BY card_type, DATEDIFF(DAY, @start, exit_time)
),
-- REMAIN: cars physically inside at END of each day
-- = active vehicles that entered this day
-- + exited vehicles that entered this day but exited on a future day
rm AS (
    SELECT card_type, DATEDIFF(DAY, @start, entry_time) + 1 AS d, COUNT(*) AS cnt
    FROM dbo.db_tbl_14_car_enterd
    WHERE entry_time >= @start AND entry_time < @end
    GROUP BY card_type, DATEDIFF(DAY, @start, entry_time)

    UNION ALL

    SELECT card_type, DATEDIFF(DAY, @start, entry_time) + 1 AS d, COUNT(*) AS cnt
    FROM dbo.db_tbl_15_car_exited
    WHERE entry_time >= @start AND entry_time < @end
      AND exit_time  >= @end
    GROUP BY card_type, DATEDIFF(DAY, @start, entry_time)
)
SELECT
    s.d,
    CONVERT(VARCHAR(10), s.day_date, 105)          AS day_label,
    s.card_type,
    ISNULL(ea.cnt, 0) + ISNULL(ee.cnt, 0)         AS total_in,
    ISNULL(ex.cnt, 0)                              AS total_out,
    ISNULL(rm2.cnt, 0)                             AS total_remain
FROM spine s
LEFT JOIN ea  ON ea.card_type  = s.card_type AND ea.d  = s.d
LEFT JOIN ee  ON ee.card_type  = s.card_type AND ee.d  = s.d
LEFT JOIN ex  ON ex.card_type  = s.card_type AND ex.d  = s.d
LEFT JOIN (
    SELECT card_type, d, SUM(cnt) AS cnt FROM rm GROUP BY card_type, d
) rm2 ON rm2.card_type = s.card_type AND rm2.d = s.d
ORDER BY s.d, s.card_type;";

                using var conn = new SqlConnection(GetDynamicConnectionString());
                var flat = await conn.QueryAsync(sql, new { start, end, days });
                var pivoted = PivotToRows(flat,
                                keySelector: r => (int)r.d,
                                labelSelector: r => (string)r.day_label);

                return Json(new
                {
                    success = true,
                    cardTypes = CardTypes,
                    rows = pivoted.rows,
                    totals = pivoted.totals,
                    averages = pivoted.averages
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMonthlyEntryExit failed");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // =====================================================================
        // YEARLY ENTRY & EXIT
        // Always shows all 12 months; grouped by month
        // =====================================================================
        [HttpPost]
        public async Task<IActionResult> GetYearlyEntryExit(int year)
        {
            try
            {
                var start = new DateTime(year, 1, 1);
                var end = start.AddYears(1);

                const string sql = @"
WITH month_nums AS (
    SELECT TOP 12 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS mn
    FROM sys.all_columns
),
card_types AS (
    SELECT card_type FROM dbo.db_tbl_10_cards_master
),
spine AS (
    SELECT mn.mn,
           UPPER(LEFT(DATENAME(MONTH, DATEADD(MONTH, mn.mn - 1, @start)), 3)) AS month_label,
           c.card_type
    FROM month_nums mn CROSS JOIN card_types c
),
ea AS (
    SELECT card_type, DATEDIFF(MONTH, @start, entry_time) + 1 AS mn, COUNT(*) AS cnt
    FROM dbo.db_tbl_14_car_enterd
    WHERE entry_time >= @start AND entry_time < @end
    GROUP BY card_type, DATEDIFF(MONTH, @start, entry_time)
),
ee AS (
    SELECT card_type, DATEDIFF(MONTH, @start, entry_time) + 1 AS mn, COUNT(*) AS cnt
    FROM dbo.db_tbl_15_car_exited
    WHERE entry_time >= @start AND entry_time < @end
    GROUP BY card_type, DATEDIFF(MONTH, @start, entry_time)
),
ex AS (
    SELECT card_type, DATEDIFF(MONTH, @start, exit_time) + 1 AS mn, COUNT(*) AS cnt
    FROM dbo.db_tbl_15_car_exited
    WHERE exit_time >= @start AND exit_time < @end
    GROUP BY card_type, DATEDIFF(MONTH, @start, exit_time)
),
-- REMAIN: cars physically inside at END of each month
-- = active vehicles that entered this month (still inside)
-- + exited vehicles that entered this month but exited in a future month
rm AS (
    SELECT card_type, DATEDIFF(MONTH, @start, entry_time) + 1 AS mn, COUNT(*) AS cnt
    FROM dbo.db_tbl_14_car_enterd
    WHERE entry_time >= @start AND entry_time < @end
    GROUP BY card_type, DATEDIFF(MONTH, @start, entry_time)

    UNION ALL

    SELECT card_type, DATEDIFF(MONTH, @start, entry_time) + 1 AS mn, COUNT(*) AS cnt
    FROM dbo.db_tbl_15_car_exited
    WHERE entry_time >= @start AND entry_time < @end
      AND exit_time  >= @end
    GROUP BY card_type, DATEDIFF(MONTH, @start, entry_time)
)
SELECT
    s.mn,
    s.month_label,
    s.card_type,
    ISNULL(ea.cnt, 0) + ISNULL(ee.cnt, 0)         AS total_in,
    ISNULL(ex.cnt, 0)                              AS total_out,
    ISNULL(rm2.cnt, 0)                             AS total_remain
FROM spine s
LEFT JOIN ea  ON ea.card_type  = s.card_type AND ea.mn  = s.mn
LEFT JOIN ee  ON ee.card_type  = s.card_type AND ee.mn  = s.mn
LEFT JOIN ex  ON ex.card_type  = s.card_type AND ex.mn  = s.mn
LEFT JOIN (
    SELECT card_type, mn, SUM(cnt) AS cnt FROM rm GROUP BY card_type, mn
) rm2 ON rm2.card_type = s.card_type AND rm2.mn = s.mn
ORDER BY s.mn, s.card_type;";

                using var conn = new SqlConnection(GetDynamicConnectionString());
                var flat = await conn.QueryAsync(sql, new { start, end });
                var pivoted = PivotToRows(flat,
                                keySelector: r => (int)r.mn,
                                labelSelector: r => (string)r.month_label);

                return Json(new
                {
                    success = true,
                    cardTypes = CardTypes,
                    rows = pivoted.rows,
                    totals = pivoted.totals,
                    averages = pivoted.averages
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetYearlyEntryExit failed");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // =====================================================================
        // OCCUPANCY — Hourly
        // =====================================================================
        [HttpPost]
        public async Task<IActionResult> GetHourlyOccupancy(DateTime? selectedDate)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                var reportDate = selectedDate?.Date ?? DateTime.Today;
                var start = reportDate;
                var end = reportDate.AddDays(1);

                const string sql = @"
;WITH hours_cte AS (
    SELECT 0 AS hr
    UNION ALL
    SELECT hr + 1 FROM hours_cte WHERE hr < 23
)
SELECT
    h.hr AS report_hour,
    RIGHT('0'+CAST(h.hr   AS VARCHAR(2)),2)+':00 - '+
    RIGHT('0'+CAST(h.hr+1 AS VARCHAR(2)),2)+':00'      AS hour_label,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_14_car_enterd e
        WHERE e.entry_time >= @start AND e.entry_time < @end
          AND DATEPART(HOUR, e.entry_time) = h.hr
    ) +
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited ex
        WHERE ex.entry_time >= @start AND ex.entry_time < @end
          AND DATEPART(HOUR, ex.entry_time) = h.hr
    ) AS total_entered,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited x
        WHERE x.exit_time >= @start AND x.exit_time < @end
          AND DATEPART(HOUR, x.exit_time) = h.hr
    ) AS total_exited,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_14_car_enterd s
        WHERE s.entry_time >= @start AND s.entry_time < @end
          AND DATEPART(HOUR, s.entry_time) <= h.hr
    ) AS still_inside,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited l
        WHERE l.entry_time >= @start AND l.entry_time < @end
          AND DATEPART(HOUR, l.entry_time) = h.hr
    ) AS left_after_entry
FROM hours_cte h
ORDER BY h.hr
OPTION (MAXRECURSION 24);";

                var data = await conn.QueryAsync(sql, new { start, end });
                return Json(new { success = true, count = data.Count(), data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetHourlyOccupancy failed");
                return Json(new { success = false, message = ex.Message, data = Array.Empty<object>() });
            }
        }

        // =====================================================================
        // OCCUPANCY — Daily
        // =====================================================================
        [HttpPost]
        public async Task<IActionResult> GetDailyOccupancy(DateTime? selectedDate)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                var start = (selectedDate?.Date) ?? DateTime.Today;
                var end = start.AddDays(1);

                const string sql = @"
SELECT
    @start AS report_date,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_14_car_enterd e
        WHERE e.entry_time >= @start AND e.entry_time < @end
    ) +
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited ex
        WHERE ex.entry_time >= @start AND ex.entry_time < @end
    ) AS total_entered,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited x
        WHERE x.exit_time >= @start AND x.exit_time < @end
    ) AS total_exited,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_14_car_enterd s
        WHERE s.entry_time >= @start AND s.entry_time < @end
    ) AS still_inside,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited l
        WHERE l.entry_time >= @start AND l.entry_time < @end
    ) AS left_after_entry;";

                var data = await conn.QueryFirstOrDefaultAsync(sql, new { start, end });
                return Json(new { success = true, data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDailyOccupancy failed");
                return Json(new { success = false, message = ex.Message, data = (object)null });
            }
        }

        // =====================================================================
        // OCCUPANCY — Monthly
        // =====================================================================
        [HttpPost]
        public async Task<IActionResult> GetMonthlyOccupancy(int month, int year)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                var start = new DateTime(year, month, 1);
                var end = start.AddMonths(1);
                int days = DateTime.DaysInMonth(year, month);

                const string sql = @"
WITH day_nums AS (
    SELECT TOP (@days) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS d
    FROM sys.all_columns
),
spine AS (
    SELECT DATEADD(DAY, d - 1, @start) AS day_date, d
    FROM day_nums
)
SELECT
    s.day_date AS report_date,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_14_car_enterd e1
        WHERE e1.entry_time >= s.day_date
          AND e1.entry_time <  DATEADD(DAY, 1, s.day_date)
    ) +
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited e2
        WHERE e2.entry_time >= s.day_date
          AND e2.entry_time <  DATEADD(DAY, 1, s.day_date)
    ) AS total_entered,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited x
        WHERE x.exit_time >= s.day_date
          AND x.exit_time <  DATEADD(DAY, 1, s.day_date)
    ) AS total_exited,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_14_car_enterd si
        WHERE si.entry_time >= s.day_date
          AND si.entry_time <  DATEADD(DAY, 1, s.day_date)
    ) AS still_inside,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited l
        WHERE l.entry_time >= s.day_date
          AND l.entry_time <  DATEADD(DAY, 1, s.day_date)
    ) AS left_after_entry
FROM spine s
ORDER BY s.day_date ASC;";

                var data = await conn.QueryAsync(sql, new { start, end, days });
                return Json(new { success = true, count = data.Count(), data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMonthlyOccupancy failed");
                return Json(new { success = false, message = ex.Message, data = Array.Empty<object>() });
            }
        }

        // =====================================================================
        // OCCUPANCY — Yearly
        // =====================================================================
        [HttpPost]
        public async Task<IActionResult> GetYearlyOccupancy(int year)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                var start = new DateTime(year, 1, 1);
                var end = start.AddYears(1);

                const string sql = @"
WITH months AS (
    SELECT  1 AS mn, 'January'   AS month_name UNION ALL
    SELECT  2,       'February'               UNION ALL
    SELECT  3,       'March'                  UNION ALL
    SELECT  4,       'April'                  UNION ALL
    SELECT  5,       'May'                    UNION ALL
    SELECT  6,       'June'                   UNION ALL
    SELECT  7,       'July'                   UNION ALL
    SELECT  8,       'August'                 UNION ALL
    SELECT  9,       'September'              UNION ALL
    SELECT 10,       'October'                UNION ALL
    SELECT 11,       'November'               UNION ALL
    SELECT 12,       'December'
),
spine AS (
    SELECT mn, month_name,
           DATEADD(MONTH, mn - 1, @start)              AS m_start,
           DATEADD(MONTH, mn,     @start)              AS m_end
    FROM months
)
SELECT
    s.mn           AS report_month,
    s.month_name,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_14_car_enterd e1
        WHERE e1.entry_time >= s.m_start AND e1.entry_time < s.m_end
    ) +
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited e2
        WHERE e2.entry_time >= s.m_start AND e2.entry_time < s.m_end
    ) AS total_entered,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited x
        WHERE x.exit_time >= s.m_start AND x.exit_time < s.m_end
    ) AS total_exited,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_14_car_enterd si
        WHERE si.entry_time >= s.m_start AND si.entry_time < s.m_end
    ) AS still_inside,
    (
        SELECT COUNT(*) FROM dbo.db_tbl_15_car_exited l
        WHERE l.entry_time >= s.m_start AND l.entry_time < s.m_end
    ) AS left_after_entry
FROM spine s
ORDER BY s.mn ASC;";

                var data = await conn.QueryAsync(sql, new { start, end });
                return Json(new { success = true, count = data.Count(), data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetYearlyOccupancy failed");
                return Json(new { success = false, message = ex.Message, data = Array.Empty<object>() });
            }
        }

        // =====================================================================
        // SHARED PIVOT HELPER
        // Converts flat SQL rows (period_key, card_type, total_in, total_out)
        // into pivoted rows the views expect.
        // =====================================================================
        private static (
            List<object> rows,
            object totals,
            object averages
        ) PivotToRows(
            IEnumerable<dynamic> flat,
            Func<dynamic, int> keySelector,
            Func<dynamic, string> labelSelector)
        {
            var rows = new List<object>();

            // Running totals per card type
            var totalIn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var totalOut = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var totalRemain = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var ct in CardTypes) { totalIn[ct] = 0; totalOut[ct] = 0; totalRemain[ct] = 0; }

            var byPeriod = flat
                .GroupBy(r => keySelector(r))
                .OrderBy(g => g.Key);

            foreach (var grp in byPeriod)
            {
                // Seed all card types with zero
                var buckets = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var ct in CardTypes)
                    buckets[ct] = new { totalIn = 0, totalOut = 0, totalRemain = 0 };

                foreach (var rec in grp)
                {
                    string ct = (string)rec.card_type;
                    if (!CardTypes.Contains(ct, StringComparer.OrdinalIgnoreCase)) continue;

                    int i = (int)rec.total_in;
                    int o = (int)rec.total_out;
                    int r = (int)rec.total_remain; // ← real remain from SQL
                    buckets[ct] = new { totalIn = i, totalOut = o, totalRemain = r };
                    totalIn[ct] += i;
                    totalOut[ct] += o;
                    totalRemain[ct] += r;
                }

                int allIn = CardTypes.Sum(ct => (int)((dynamic)buckets[ct]).totalIn);
                int allOut = CardTypes.Sum(ct => (int)((dynamic)buckets[ct]).totalOut);
                int allRemain = CardTypes.Sum(ct => (int)((dynamic)buckets[ct]).totalRemain);

                rows.Add(new
                {
                    periodLabel = labelSelector(grp.First()),
                    cardBuckets = buckets,
                    allTotalIn = allIn,
                    allTotalOut = allOut,
                    allTotalRemain = allRemain
                });
            }

            // ── TOTAL row ──────────────────────────────────────────────────
            var totalsDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var ct in CardTypes)
                totalsDict[ct] = new
                {
                    totalIn = totalIn[ct],
                    totalOut = totalOut[ct],
                    totalRemain = totalRemain[ct]  // ← real remain
                };

            int grandIn = totalIn.Values.Sum();
            int grandOut = totalOut.Values.Sum();
            int grandRemain = totalRemain.Values.Sum();

            // ── AVERAGE row — divide by non-zero rows only ─────────────────
            int nz = rows.Count(r =>
                (int)((dynamic)r).allTotalIn > 0 ||
                (int)((dynamic)r).allTotalOut > 0);
            if (nz == 0) nz = 1;

            var avgsDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var ct in CardTypes)
                avgsDict[ct] = new
                {
                    totalIn = (int)Math.Round(totalIn[ct] / (double)nz),
                    totalOut = (int)Math.Round(totalOut[ct] / (double)nz),
                    totalRemain = (int)Math.Round(totalRemain[ct] / (double)nz)
                };

            return (
                rows,
                totals: (object)new
                {
                    cardBuckets = totalsDict,
                    allTotalIn = grandIn,
                    allTotalOut = grandOut,
                    allTotalRemain = grandRemain
                },
                averages: (object)new
                {
                    cardBuckets = avgsDict,
                    allTotalIn = (int)Math.Round(grandIn / (double)nz),
                    allTotalOut = (int)Math.Round(grandOut / (double)nz),
                    allTotalRemain = (int)Math.Round(grandRemain / (double)nz)
                }
            );
        }

        // =====================================================================
        // BAY UTILIZATION REPORT
        // Source: db_tbl_15_car_exited
        // Metrics: count, parked hours, avg hours, charges, charged/hr
        // Card types: loaded dynamically from exit table
        // =====================================================================

        [HttpPost]
        public async Task<IActionResult> GetBayUtilization(
     DateTime? fromDate,
     DateTime? toDate,
     string cardType = "ALL")
        {
            try
            {
                var start = fromDate?.Date ?? DateTime.Today;
                var end = (toDate?.Date ?? DateTime.Today).AddDays(1);
                string? normalizedCardTypeFilter = string.IsNullOrWhiteSpace(cardType) || cardType == "ALL"
                    ? null
                    : cardType.Trim().ToUpperInvariant();

                using var conn = new SqlConnection(GetDynamicConnectionString());

                const string normalizeCardTypeSql = @"
            CASE
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) LIKE '%SEASON%' THEN 'SEASONAL'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('DAILY ACCESS', 'DAILY_ACCESS', 'DAILYACCESS') THEN 'DAILY_ACCESS'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('CASH CARD', 'CASH_CARD', 'CASHCARD') THEN 'CASH_CARD'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('TEMP', 'TEMPORARY') THEN 'Temporary'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) = 'VIP' THEN 'VIP'
                ELSE UPPER(LTRIM(RTRIM(e.card_type)))
            END";

                var cardTypesSql = $@"
            SELECT DISTINCT cardtype
            FROM
            (
                SELECT {normalizeCardTypeSql} AS cardtype
                FROM dbo.db_tbl_15_car_exited e
                WHERE e.card_type IS NOT NULL
                  AND LTRIM(RTRIM(e.card_type)) <> ''
                  AND e.created_on >= @start
                  AND e.created_on < @end
                  AND e.exit_time IS NOT NULL
            ) x
            ORDER BY cardtype;";

                var cardTypes = (await conn.QueryAsync<string>(cardTypesSql, new { start, end })).ToList();

                var sql = $@"
            WITH base AS
            (
                SELECT
                    CAST(e.created_on AS DATE) AS reportdate,
                    DATENAME(WEEKDAY, e.created_on) AS dayofweek,
                    {normalizeCardTypeSql} AS cardtype,
                    e.entry_time,
                    e.exit_time,
                    ISNULL(e.charges, 0) AS charges,
                    ISNULL(e.paid_amount, 0) AS paid_amount,
                    ISNULL(e.discount_amount, 0) AS discount_amount,
                    ISNULL(e.chargeable_minutes, 0) AS chargeable_minutes,
                    ISNULL(e.free_minutes, 0) AS free_minutes
                FROM dbo.db_tbl_15_car_exited e
                WHERE e.created_on >= @start
                  AND e.created_on < @end
                  AND e.exit_time IS NOT NULL
            )
            SELECT
                reportdate,
                dayofweek,
                cardtype,
                COUNT(*) AS vehiclecount,
                SUM(CAST(DATEDIFF(MINUTE, entry_time, exit_time) AS FLOAT) / 60.0) AS totalparkedhours,
                SUM(charges) AS totalcharges,
                SUM(paid_amount) AS totalpaid,
                SUM(discount_amount) AS totaldiscount,
                SUM(chargeable_minutes) AS totalchargeableminutes,
                SUM(free_minutes) AS totalfreeminutes
            FROM base
            WHERE (@normalizedCardTypeFilter IS NULL OR cardtype = @normalizedCardTypeFilter)
            GROUP BY reportdate, dayofweek, cardtype
            ORDER BY reportdate ASC, cardtype ASC;";

                var flat = (await conn.QueryAsync(sql, new { start, end, normalizedCardTypeFilter })).ToList();

                var grouped = flat
                    .GroupBy(r => ((DateTime)r.reportdate).ToString("yyyy-MM-dd"))
                    .OrderBy(g => g.Key)
                    .Select(g =>
                    {
                        var dateRows = g.ToList();

                        var byType = cardTypes.ToDictionary(
                            ct => ct,
                            ct =>
                            {
                                var r = dateRows.FirstOrDefault(x => (string)x.cardtype == ct);
                                return (object)new
                                {
                                    vehicleCount = r == null ? 0 : (int)r.vehiclecount,
                                    totalParkedHours = r == null ? 0.0 : Math.Round((double)r.totalparkedhours, 2),
                                    avgParkedHours = r == null || (int)r.vehiclecount == 0 ? 0.0 : Math.Round((double)r.totalparkedhours / (int)r.vehiclecount, 2),
                                    totalCharges = r == null ? 0.0 : Math.Round((double)r.totalcharges, 2),
                                    totalPaid = r == null ? 0.0 : Math.Round((double)r.totalpaid, 2),
                                    totalDiscount = r == null ? 0.0 : Math.Round((double)r.totaldiscount, 2),
                                    chargesPerHour = r == null || (double)r.totalparkedhours <= 0 ? 0.0 : Math.Round((double)r.totalcharges / (double)r.totalparkedhours, 2),
                                    avgChargesPerVehicle = r == null || (int)r.vehiclecount == 0 ? 0.0 : Math.Round((double)r.totalcharges / (int)r.vehiclecount, 2),
                                    chargeableMinutes = r == null ? 0L : Convert.ToInt64(r.totalchargeableminutes),
                                    freeMinutes = r == null ? 0L : Convert.ToInt64(r.totalfreeminutes)
                                };
                            });

                        int sumCount = dateRows.Sum(r => (int)r.vehiclecount);
                        double sumHours = dateRows.Sum(r => (double)r.totalparkedhours);
                        double sumChg = dateRows.Sum(r => (double)r.totalcharges);
                        double sumPaid = dateRows.Sum(r => (double)r.totalpaid);

                        return new
                        {
                            exitDate = g.Key,
                            dayOfWeek = (string)dateRows[0].dayofweek,
                            byCardType = byType,
                            dayTotal = new
                            {
                                vehicleCount = sumCount,
                                totalParkedHours = Math.Round(sumHours, 2),
                                avgParkedHours = sumCount > 0 ? Math.Round(sumHours / sumCount, 2) : 0.0,
                                totalCharges = Math.Round(sumChg, 2),
                                totalPaid = Math.Round(sumPaid, 2),
                                chargesPerHour = sumHours > 0 ? Math.Round(sumChg / sumHours, 2) : 0.0,
                                avgChargesPerVehicle = sumCount > 0 ? Math.Round(sumChg / sumCount, 2) : 0.0
                            }
                        };
                    }).ToList();

                var summaryByCardType = cardTypes.ToDictionary(
                    ct => ct,
                    ct =>
                    {
                        var rows = flat.Where(r => (string)r.cardtype == ct).ToList();
                        int count = rows.Sum(r => (int)r.vehiclecount);
                        double hrs = rows.Sum(r => (double)r.totalparkedhours);
                        double chg = rows.Sum(r => (double)r.totalcharges);
                        double pd = rows.Sum(r => (double)r.totalpaid);
                        double disc = rows.Sum(r => (double)r.totaldiscount);

                        return (object)new
                        {
                            vehicleCount = count,
                            totalParkedHours = Math.Round(hrs, 2),
                            totalCharges = Math.Round(chg, 2),
                            totalPaid = Math.Round(pd, 2),
                            totalDiscount = Math.Round(disc, 2),
                            avgChargesPerVehicle = count > 0 ? Math.Round(chg / count, 2) : 0.0,
                            chargesPerHour = hrs > 0 ? Math.Round(chg / hrs, 2) : 0.0
                        };
                    });

                int grandCount = flat.Sum(r => (int)r.vehiclecount);
                double grandHours = flat.Sum(r => (double)r.totalparkedhours);
                double grandCharges = flat.Sum(r => (double)r.totalcharges);
                double grandPaid = flat.Sum(r => (double)r.totalpaid);
                double avgBayUtilization = Math.Round(grandHours / 450.0, 2);

                return Json(new
                {
                    success = true,
                    cardTypes,
                    rows = grouped,
                    summaryByCardType,
                    grandTotal = new
                    {
                        vehicleCount = grandCount,
                        totalParkedHours = Math.Round(grandHours, 2),
                        avgParkedHours = grandCount > 0 ? Math.Round(grandHours / grandCount, 2) : 0.0,
                        totalCharges = Math.Round(grandCharges, 2),
                        totalPaid = Math.Round(grandPaid, 2),
                        avgChargesPerVehicle = grandCount > 0 ? Math.Round(grandCharges / grandCount, 2) : 0.0,
                        chargesPerHour = grandHours > 0 ? Math.Round(grandCharges / grandHours, 2) : 0.0,
                        avgBayUtilization = avgBayUtilization
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetBayUtilization failed");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ExportBayUtilizationExcel(
   DateTime? fromDate,
   DateTime? toDate,
   string cardType = "ALL",
   string groupBy = "datetype")
        {
            try
            {
                var start = fromDate?.Date ?? DateTime.Today;
                var end = (toDate?.Date ?? DateTime.Today).AddDays(1);
                string? normalizedCardTypeFilter = string.IsNullOrWhiteSpace(cardType) || cardType == "ALL"
                    ? null
                    : cardType.Trim().ToUpperInvariant();

                using var conn = new SqlConnection(GetDynamicConnectionString());

                const string normalizeCardTypeSql = @"
            CASE
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) LIKE '%SEASON%' THEN 'SEASONAL'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('DAILY ACCESS', 'DAILY_ACCESS', 'DAILYACCESS') THEN 'DAILY_ACCESS'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('CASH CARD', 'CASH_CARD', 'CASHCARD') THEN 'CASH_CARD'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('TEMP', 'TEMPORARY') THEN 'Temporary'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) = 'VIP' THEN 'VIP'
                ELSE UPPER(LTRIM(RTRIM(e.card_type)))
            END";

                var sql = $@"
            WITH base AS
            (
                SELECT
                    CAST(e.created_on AS DATE) AS exitdate,
                    DATENAME(WEEKDAY, e.created_on) AS dayofweek,
                    {normalizeCardTypeSql} AS cardtype,
                    e.entry_time,
                    e.exit_time,
                    ISNULL(e.charges, 0) AS charges,
                    ISNULL(e.paid_amount, 0) AS paid_amount
                FROM dbo.db_tbl_15_car_exited e
                WHERE e.created_on >= @start
                  AND e.created_on < @end
                  AND e.exit_time IS NOT NULL
            )
            SELECT
                exitdate,
                dayofweek,
                cardtype,
                COUNT(*) AS vehiclecount,
                ISNULL(SUM(CAST(DATEDIFF(MINUTE, entry_time, exit_time) AS FLOAT) / 60.0), 0) AS totalparkedhours,
                ISNULL(SUM(charges), 0) AS totalcharges,
                ISNULL(SUM(paid_amount), 0) AS totalpaid
            FROM base
            WHERE (@normalizedCardTypeFilter IS NULL OR cardtype = @normalizedCardTypeFilter)
            GROUP BY exitdate, dayofweek, cardtype
            ORDER BY exitdate, cardtype;";

                var flat = (await conn.QueryAsync(sql, new { start, end, normalizedCardTypeFilter })).ToList();

                var cardTypes = flat
                    .Select(r => (string)r.cardtype)
                    .Distinct()
                    .OrderBy(t => t)
                    .ToList();

                var dates = flat
                    .GroupBy(r => new { d = ((DateTime)r.exitdate).ToString("yyyy-MM-dd"), dw = (string)r.dayofweek })
                    .OrderBy(g => g.Key.d)
                    .ToList();

                var lookup = flat.ToDictionary(
                    r => $"{((DateTime)r.exitdate):yyyy-MM-dd}|{(string)r.cardtype}",
                    r => r);

                var wb = new NPOI.HSSF.UserModel.HSSFWorkbook();
                var sheet = wb.CreateSheet("Daily Parking Utilization");
                sheet.CreateFreezePane(2, 3);

                var dfmt = wb.CreateDataFormat();

                ICellStyle MakeStyle(bool bold = false, short bgColor = -1, bool right = false, bool center = false, string? numFormat = null, short fontColor = -1,
                    bool leftBold = false, bool rightBold = false,
                    NPOI.SS.UserModel.BorderStyle topBorder = NPOI.SS.UserModel.BorderStyle.Thin,
                    NPOI.SS.UserModel.BorderStyle bottomBorder = NPOI.SS.UserModel.BorderStyle.Thin)
                {
                    var st = wb.CreateCellStyle();
                    var ft = wb.CreateFont();
                    ft.IsBold = bold;
                    ft.FontHeightInPoints = 9;
                    if (fontColor >= 0) ft.Color = fontColor;
                    st.SetFont(ft);
                    if (bgColor >= 0)
                    {
                        st.FillForegroundColor = bgColor;
                        st.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground;
                    }
                    st.Alignment = center ? NPOI.SS.UserModel.HorizontalAlignment.Center
                                          : right ? NPOI.SS.UserModel.HorizontalAlignment.Right
                                                  : NPOI.SS.UserModel.HorizontalAlignment.Left;
                    st.BorderTop = topBorder;
                    st.BorderBottom = bottomBorder;
                    st.BorderLeft = leftBold ? NPOI.SS.UserModel.BorderStyle.Medium : NPOI.SS.UserModel.BorderStyle.Thin;
                    st.BorderRight = rightBold ? NPOI.SS.UserModel.BorderStyle.Medium : NPOI.SS.UserModel.BorderStyle.Thin;
                    if (numFormat != null) st.DataFormat = dfmt.GetFormat(numFormat);
                    return st;
                }

                var stTitle = MakeStyle(bold: true, fontColor: NPOI.SS.UserModel.IndexedColors.DarkBlue.Index);
                var stDate = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.LightCornflowerBlue.Index);
                var stSubHdr = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index);
                var stSubHdrR = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index, right: true);
                var stData = MakeStyle();
                var stDataR = MakeStyle(right: true, numFormat: "#,##0.00");
                var stDataRi = MakeStyle(right: true, numFormat: "#,##0");
                var stTotalL = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index);
                var stTotal = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index, right: true, numFormat: "#,##0.00");
                var stTotalI = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index, right: true, numFormat: "#,##0");

                var stCardHdrFirst = MakeStyle(bold: true, center: true, bgColor: NPOI.SS.UserModel.IndexedColors.DarkBlue.Index, fontColor: NPOI.SS.UserModel.IndexedColors.White.Index, leftBold: true);
                var stCardHdrMid = MakeStyle(bold: true, center: true, bgColor: NPOI.SS.UserModel.IndexedColors.DarkBlue.Index, fontColor: NPOI.SS.UserModel.IndexedColors.White.Index);
                var stCardHdrLast = MakeStyle(bold: true, center: true, bgColor: NPOI.SS.UserModel.IndexedColors.DarkBlue.Index, fontColor: NPOI.SS.UserModel.IndexedColors.White.Index, rightBold: true);

                var stSubHdrFirst = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index, right: true, leftBold: true);
                var stSubHdrLast = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index, right: true, rightBold: true);

                var stDataFirst = MakeStyle(right: true, numFormat: "#,##0", leftBold: true);
                var stDataMid = MakeStyle(right: true, numFormat: "#,##0.00");
                var stDataLast = MakeStyle(right: true, numFormat: "#,##0.00", rightBold: true);

                var stTotalFirst = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index, right: true, numFormat: "#,##0", leftBold: true);
                var stTotalMid = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index, right: true, numFormat: "#,##0.00");
                var stTotalLast = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index, right: true, numFormat: "#,##0.00", rightBold: true);

                var stDateFirst = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.LightCornflowerBlue.Index, leftBold: true);

                void S(IRow row, int col, string val, ICellStyle st)
                {
                    var c = row.CreateCell(col);
                    c.SetCellValue(val);
                    c.CellStyle = st;
                }

                void N(IRow row, int col, double val, ICellStyle st)
                {
                    var c = row.CreateCell(col);
                    c.SetCellValue(val);
                    c.CellStyle = st;
                }

                int rowIdx = 0;
                int totalCols = 2 + (cardTypes.Count * 4) + 4 + 1;

                var titleRow = sheet.CreateRow(rowIdx++);
                S(titleRow, 0, $"Daily Parking Utilization Report ({start:dd-MMM-yyyy} to {end.AddDays(-1):dd-MMM-yyyy}) | Card Type: {normalizedCardTypeFilter ?? "ALL"}", stTitle);
                sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(0, 0, 0, totalCols - 1));

                var grpRow = sheet.CreateRow(rowIdx++);
                S(grpRow, 0, "", stSubHdr);
                S(grpRow, 1, "", stSubHdr);
                int col = 2;

                void WriteGroupHeader(IRow row, int startCol, string label)
                {
                    var c0 = row.CreateCell(startCol);
                    c0.SetCellValue(label);
                    c0.CellStyle = stCardHdrFirst;

                    for (int x = 1; x <= 2; x++)
                    {
                        var cm = row.CreateCell(startCol + x);
                        cm.SetCellValue("");
                        cm.CellStyle = stCardHdrMid;
                    }

                    var cLast = row.CreateCell(startCol + 3);
                    cLast.SetCellValue("");
                    cLast.CellStyle = stCardHdrLast;

                    sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(row.RowNum, row.RowNum, startCol, startCol + 3));
                }

                foreach (var ct in cardTypes)
                {
                    WriteGroupHeader(grpRow, col, ct);
                    col += 4;
                }

                WriteGroupHeader(grpRow, col, "GRAND TOTAL");
                col += 4;

                var avgUtilCell = grpRow.CreateCell(col);
                avgUtilCell.SetCellValue("Avg Bay Utilization");
                avgUtilCell.CellStyle = stCardHdrLast;

                var subRow = sheet.CreateRow(rowIdx++);
                S(subRow, 0, "DATE", stSubHdr);
                S(subRow, 1, "DAY", stSubHdr);

                col = 2;
                int numGroups = cardTypes.Count + 1;
                for (int g = 0; g < numGroups; g++)
                {
                    S(subRow, col++, "Vehicles", stSubHdrFirst);
                    S(subRow, col++, "Total Hrs", stSubHdrR);
                    S(subRow, col++, "Charges", stSubHdrR);
                    S(subRow, col++, "Paid", stSubHdrLast);
                }
                S(subRow, col, "(Hrs/450)", stSubHdrLast);

                sheet.SetColumnWidth(0, 4200);
                sheet.SetColumnWidth(1, 4000);
                for (int i = 2; i < totalCols; i++) sheet.SetColumnWidth(i, 3200);

                var gtVehicles = cardTypes.ToDictionary(t => t, t => 0);
                var gtHours = cardTypes.ToDictionary(t => t, t => 0.0);
                var gtCharges = cardTypes.ToDictionary(t => t, t => 0.0);
                var gtPaid = cardTypes.ToDictionary(t => t, t => 0.0);

                foreach (var dateGroup in dates)
                {
                    var dr = sheet.CreateRow(rowIdx++);
                    S(dr, 0, dateGroup.Key.d, stDate);
                    S(dr, 1, dateGroup.Key.dw, stDate);

                    col = 2;
                    int rowTotalVeh = 0;
                    double rowTotalHrs = 0, rowTotalChg = 0, rowTotalPaid = 0;

                    foreach (var ct in cardTypes)
                    {
                        var key = $"{dateGroup.Key.d}|{ct}";
                        int vc = 0;
                        double hrs = 0, chg = 0, pd = 0;

                        if (lookup.TryGetValue(key, out var r))
                        {
                            vc = (int)r.vehiclecount;
                            hrs = Math.Round((double)r.totalparkedhours, 2);
                            chg = Math.Round(Convert.ToDouble(r.totalcharges), 2);
                            pd = Math.Round(Convert.ToDouble(r.totalpaid), 2);
                        }

                        N(dr, col++, vc, stDataFirst);
                        N(dr, col++, hrs, stDataMid);
                        N(dr, col++, chg, stDataMid);
                        N(dr, col++, pd, stDataLast);

                        rowTotalVeh += vc;
                        rowTotalHrs += hrs;
                        rowTotalChg += chg;
                        rowTotalPaid += pd;

                        gtVehicles[ct] += vc;
                        gtHours[ct] += hrs;
                        gtCharges[ct] += chg;
                        gtPaid[ct] += pd;
                    }

                    N(dr, col++, rowTotalVeh, stTotalFirst);
                    N(dr, col++, Math.Round(rowTotalHrs, 2), stTotalMid);
                    N(dr, col++, Math.Round(rowTotalChg, 2), stTotalMid);
                    N(dr, col++, Math.Round(rowTotalPaid, 2), stTotalLast);

                    double rowAvgUtil = Math.Round(rowTotalHrs / 450.0, 2);
                    N(dr, col, rowAvgUtil, stTotalLast);
                }

                var gtRow = sheet.CreateRow(rowIdx++);
                S(gtRow, 0, "GRAND TOTAL", stTotalL);
                S(gtRow, 1, "", stTotalL);

                col = 2;
                int finalTotalVeh = 0;
                double finalTotalHrs = 0, finalTotalChg = 0, finalTotalPaid = 0;

                foreach (var ct in cardTypes)
                {
                    N(gtRow, col++, gtVehicles[ct], stTotalFirst);
                    N(gtRow, col++, Math.Round(gtHours[ct], 2), stTotalMid);
                    N(gtRow, col++, Math.Round(gtCharges[ct], 2), stTotalMid);
                    N(gtRow, col++, Math.Round(gtPaid[ct], 2), stTotalLast);

                    finalTotalVeh += gtVehicles[ct];
                    finalTotalHrs += gtHours[ct];
                    finalTotalChg += gtCharges[ct];
                    finalTotalPaid += gtPaid[ct];
                }

                N(gtRow, col++, finalTotalVeh, stTotalFirst);
                N(gtRow, col++, Math.Round(finalTotalHrs, 2), stTotalMid);
                N(gtRow, col++, Math.Round(finalTotalChg, 2), stTotalMid);
                N(gtRow, col++, Math.Round(finalTotalPaid, 2), stTotalLast);

                double finalAvgUtil = Math.Round(finalTotalHrs / 450.0, 2);
                N(gtRow, col, finalAvgUtil, stTotalLast);

                using var ms = new System.IO.MemoryStream();
                wb.Write(ms);
                string fileName = $"DailyParkingUtilization_{start:yyyyMMdd}_{end.AddDays(-1):yyyyMMdd}.xls";
                return File(ms.ToArray(), "application/vnd.ms-excel", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExportBayUtilizationExcel failed");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // =============================================
        // PARKING DURATION ANALYSIS REPORT (BY CARD TYPE)
        // =============================================

        [HttpGet]
        public IActionResult ParkingDuration()
        {
            return View("~/Views/Reports/ParkingDuration.cshtml");
        }

        [HttpGet]
        public async Task<IActionResult> GetCardTypes()
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());

                const string sql = @"
            SELECT DISTINCT UPPER(LTRIM(RTRIM(card_type))) AS cardtype
            FROM dbo.db_tbl_15_car_exited
            WHERE card_type IS NOT NULL AND LTRIM(RTRIM(card_type)) <> ''
            ORDER BY UPPER(LTRIM(RTRIM(card_type)))";

                var cardTypes = (await conn.QueryAsync<string>(sql)).ToList();

                return Json(new { success = true, cardTypes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetCardTypes failed");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> GetParkingDuration(DateTime? fromDate, DateTime? toDate, string? cardType)
        {
            try
            {
                var start = fromDate?.Date ?? DateTime.Today;
                var end = (toDate?.Date ?? DateTime.Today).AddDays(1);
                var normalizedCardTypeFilter = string.IsNullOrWhiteSpace(cardType)
                    ? null
                    : cardType.Trim().ToUpperInvariant();

                using var conn = new SqlConnection(GetDynamicConnectionString());

                const string normalizeCardTypeSql = @"
            CASE
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) LIKE '%SEASON%' THEN 'SEASONAL'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('DAILY ACCESS', 'DAILY_ACCESS', 'DAILYACCESS') THEN 'DAILY_ACCESS'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('CASH CARD', 'CASH_CARD', 'CASHCARD') THEN 'CASH_CARD'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('TEMP', 'TEMPORARY') THEN 'Temporary'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) = 'VIP' THEN 'VIP'
                ELSE UPPER(LTRIM(RTRIM(e.card_type)))
            END";

                var cardTypesSql = $@"
            SELECT DISTINCT cardtype
            FROM
            (
                SELECT {normalizeCardTypeSql} AS cardtype
                FROM dbo.db_tbl_15_car_exited e
                WHERE e.card_type IS NOT NULL
                  AND LTRIM(RTRIM(e.card_type)) <> ''
                  AND e.created_on >= @start
                  AND e.created_on < @end
                  AND e.exit_time IS NOT NULL
            ) x
            ORDER BY cardtype;";

                var cardTypes = (await conn.QueryAsync<string>(cardTypesSql, new { start, end })).ToList();

                var sql = $@"
            WITH base AS
            (
                SELECT
                    CAST(e.created_on AS DATE) AS reportdate,
                    DATENAME(WEEKDAY, e.created_on) AS dayofweek,
                    {normalizeCardTypeSql} AS cardtype,
                    DATEDIFF(MINUTE, e.entry_time, e.exit_time) AS park_minutes
                FROM dbo.db_tbl_15_car_exited e
                WHERE e.created_on >= @start
                  AND e.created_on < @end
                  AND e.exit_time IS NOT NULL
            )
            SELECT
                reportdate,
                dayofweek,
                cardtype,
                SUM(CASE WHEN park_minutes < 15 THEN 1 ELSE 0 END) AS parked_0_to_15min,
                SUM(CASE WHEN park_minutes >= 15 AND park_minutes < 30 THEN 1 ELSE 0 END) AS parked_15_to_30min,
                SUM(CASE WHEN park_minutes >= 30 AND park_minutes < 60 THEN 1 ELSE 0 END) AS parked_30min_to_1hr,
                SUM(CASE WHEN park_minutes >= 60 AND park_minutes < 120 THEN 1 ELSE 0 END) AS parked_1hr_to_2hr,
                SUM(CASE WHEN park_minutes >= 120 AND park_minutes < 180 THEN 1 ELSE 0 END) AS parked_2hr_to_3hr,
                SUM(CASE WHEN park_minutes >= 180 AND park_minutes < 240 THEN 1 ELSE 0 END) AS parked_3hr_to_4hr,
                SUM(CASE WHEN park_minutes >= 240 AND park_minutes < 300 THEN 1 ELSE 0 END) AS parked_4hr_to_5hr,
                SUM(CASE WHEN park_minutes >= 300 AND park_minutes < 360 THEN 1 ELSE 0 END) AS parked_5hr_to_6hr,
                SUM(CASE WHEN park_minutes >= 360 THEN 1 ELSE 0 END) AS parked_above_6hr,
                COUNT(*) AS total_vehicles
            FROM base
            WHERE (@normalizedCardTypeFilter IS NULL OR cardtype = @normalizedCardTypeFilter)
            GROUP BY reportdate, dayofweek, cardtype
            ORDER BY reportdate ASC, cardtype ASC;";

                var flat = (await conn.QueryAsync(sql, new { start, end, normalizedCardTypeFilter })).ToList();

                var grouped = flat
                    .GroupBy(r => new
                    {
                        date = ((DateTime)r.reportdate).ToString("yyyy-MM-dd"),
                        dayOfWeek = (string)r.dayofweek
                    })
                    .OrderBy(g => g.Key.date)
                    .Select(g =>
                    {
                        var dateRows = g.ToList();
                        var byCardType = new Dictionary<string, object>();

                        foreach (var ct in cardTypes)
                        {
                            var row = dateRows.FirstOrDefault(r => (string)r.cardtype == ct);
                            if (row != null)
                            {
                                byCardType[ct] = new
                                {
                                    parked_0_to_15min = (int)row.parked_0_to_15min,
                                    parked_15_to_30min = (int)row.parked_15_to_30min,
                                    parked_30min_to_1hr = (int)row.parked_30min_to_1hr,
                                    parked_1hr_to_2hr = (int)row.parked_1hr_to_2hr,
                                    parked_2hr_to_3hr = (int)row.parked_2hr_to_3hr,
                                    parked_3hr_to_4hr = (int)row.parked_3hr_to_4hr,
                                    parked_4hr_to_5hr = (int)row.parked_4hr_to_5hr,
                                    parked_5hr_to_6hr = (int)row.parked_5hr_to_6hr,
                                    parked_above_6hr = (int)row.parked_above_6hr,
                                    total_vehicles = (int)row.total_vehicles
                                };
                            }
                            else
                            {
                                byCardType[ct] = new
                                {
                                    parked_0_to_15min = 0,
                                    parked_15_to_30min = 0,
                                    parked_30min_to_1hr = 0,
                                    parked_1hr_to_2hr = 0,
                                    parked_2hr_to_3hr = 0,
                                    parked_3hr_to_4hr = 0,
                                    parked_4hr_to_5hr = 0,
                                    parked_5hr_to_6hr = 0,
                                    parked_above_6hr = 0,
                                    total_vehicles = 0
                                };
                            }
                        }

                        int dayTotal_0_15 = dateRows.Sum(r => (int)r.parked_0_to_15min);
                        int dayTotal_15_30 = dateRows.Sum(r => (int)r.parked_15_to_30min);
                        int dayTotal_30_60 = dateRows.Sum(r => (int)r.parked_30min_to_1hr);
                        int dayTotal_60_120 = dateRows.Sum(r => (int)r.parked_1hr_to_2hr);
                        int dayTotal_120_180 = dateRows.Sum(r => (int)r.parked_2hr_to_3hr);
                        int dayTotal_180_240 = dateRows.Sum(r => (int)r.parked_3hr_to_4hr);
                        int dayTotal_240_300 = dateRows.Sum(r => (int)r.parked_4hr_to_5hr);
                        int dayTotal_300_360 = dateRows.Sum(r => (int)r.parked_5hr_to_6hr);
                        int dayTotal_360_plus = dateRows.Sum(r => (int)r.parked_above_6hr);
                        int dayTotal_all = dateRows.Sum(r => (int)r.total_vehicles);

                        return new
                        {
                            exitDate = g.Key.date,
                            dayOfWeek = g.Key.dayOfWeek,
                            byCardType,
                            dayTotal = new
                            {
                                parked_0_to_15min = dayTotal_0_15,
                                parked_15_to_30min = dayTotal_15_30,
                                parked_30min_to_1hr = dayTotal_30_60,
                                parked_1hr_to_2hr = dayTotal_60_120,
                                parked_2hr_to_3hr = dayTotal_120_180,
                                parked_3hr_to_4hr = dayTotal_180_240,
                                parked_4hr_to_5hr = dayTotal_240_300,
                                parked_5hr_to_6hr = dayTotal_300_360,
                                parked_above_6hr = dayTotal_360_plus,
                                total_vehicles = dayTotal_all
                            }
                        };
                    }).ToList();

                var grandTotalByCardType = new Dictionary<string, object>();
                foreach (var ct in cardTypes)
                {
                    var ctRows = flat.Where(r => (string)r.cardtype == ct).ToList();
                    grandTotalByCardType[ct] = new
                    {
                        parked_0_to_15min = ctRows.Sum(r => (int)r.parked_0_to_15min),
                        parked_15_to_30min = ctRows.Sum(r => (int)r.parked_15_to_30min),
                        parked_30min_to_1hr = ctRows.Sum(r => (int)r.parked_30min_to_1hr),
                        parked_1hr_to_2hr = ctRows.Sum(r => (int)r.parked_1hr_to_2hr),
                        parked_2hr_to_3hr = ctRows.Sum(r => (int)r.parked_2hr_to_3hr),
                        parked_3hr_to_4hr = ctRows.Sum(r => (int)r.parked_3hr_to_4hr),
                        parked_4hr_to_5hr = ctRows.Sum(r => (int)r.parked_4hr_to_5hr),
                        parked_5hr_to_6hr = ctRows.Sum(r => (int)r.parked_5hr_to_6hr),
                        parked_above_6hr = ctRows.Sum(r => (int)r.parked_above_6hr),
                        total_vehicles = ctRows.Sum(r => (int)r.total_vehicles)
                    };
                }

                int gt_0_15 = flat.Sum(r => (int)r.parked_0_to_15min);
                int gt_15_30 = flat.Sum(r => (int)r.parked_15_to_30min);
                int gt_30_60 = flat.Sum(r => (int)r.parked_30min_to_1hr);
                int gt_60_120 = flat.Sum(r => (int)r.parked_1hr_to_2hr);
                int gt_120_180 = flat.Sum(r => (int)r.parked_2hr_to_3hr);
                int gt_180_240 = flat.Sum(r => (int)r.parked_3hr_to_4hr);
                int gt_240_300 = flat.Sum(r => (int)r.parked_4hr_to_5hr);
                int gt_300_360 = flat.Sum(r => (int)r.parked_5hr_to_6hr);
                int gt_360_plus = flat.Sum(r => (int)r.parked_above_6hr);
                int gt_total = flat.Sum(r => (int)r.total_vehicles);

                return Json(new
                {
                    success = true,
                    cardTypes,
                    rows = grouped,
                    grandTotalByCardType,
                    grandTotal = new
                    {
                        parked_0_to_15min = gt_0_15,
                        parked_15_to_30min = gt_15_30,
                        parked_30min_to_1hr = gt_30_60,
                        parked_1hr_to_2hr = gt_60_120,
                        parked_2hr_to_3hr = gt_120_180,
                        parked_3hr_to_4hr = gt_180_240,
                        parked_4hr_to_5hr = gt_240_300,
                        parked_5hr_to_6hr = gt_300_360,
                        parked_above_6hr = gt_360_plus,
                        total_vehicles = gt_total
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetParkingDuration failed");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ExportParkingDurationExcel(DateTime? fromDate, DateTime? toDate, string? cardType)
        {
            try
            {
                var start = fromDate?.Date ?? DateTime.Today;
                var end = (toDate?.Date ?? DateTime.Today).AddDays(1);
                var normalizedCardTypeFilter = string.IsNullOrWhiteSpace(cardType)
                    ? null
                    : cardType.Trim().ToUpperInvariant();

                using var conn = new SqlConnection(GetDynamicConnectionString());

                const string normalizeCardTypeSql = @"
            CASE
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) LIKE '%SEASON%' THEN 'SEASONAL'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('DAILY ACCESS', 'DAILY_ACCESS', 'DAILYACCESS') THEN 'DAILY_ACCESS'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('CASH CARD', 'CASH_CARD', 'CASHCARD') THEN 'CASH_CARD'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) IN ('TEMP', 'TEMPORARY') THEN 'Temporary'
                WHEN UPPER(LTRIM(RTRIM(e.card_type))) = 'VIP' THEN 'VIP'
                ELSE UPPER(LTRIM(RTRIM(e.card_type)))
            END";

                var cardTypesSql = $@"
            SELECT DISTINCT cardtype
            FROM
            (
                SELECT {normalizeCardTypeSql} AS cardtype
                FROM dbo.db_tbl_15_car_exited e
                WHERE e.card_type IS NOT NULL
                  AND LTRIM(RTRIM(e.card_type)) <> ''
                  AND e.created_on >= @start
                  AND e.created_on < @end
                  AND e.exit_time IS NOT NULL
            ) x
            ORDER BY cardtype;";

                var allCardTypes = (await conn.QueryAsync<string>(cardTypesSql, new { start, end })).ToList();

                var sql = $@"
            WITH base AS
            (
                SELECT
                    CAST(e.created_on AS DATE) AS exitdate,
                    DATENAME(WEEKDAY, e.created_on) AS dayofweek,
                    {normalizeCardTypeSql} AS cardtype,
                    DATEDIFF(MINUTE, e.entry_time, e.exit_time) AS park_minutes
                FROM dbo.db_tbl_15_car_exited e
                WHERE e.created_on >= @start
                  AND e.created_on < @end
                  AND e.exit_time IS NOT NULL
            )
            SELECT
                exitdate,
                dayofweek,
                cardtype,
                SUM(CASE WHEN park_minutes < 15 THEN 1 ELSE 0 END) AS parked_0_to_15min,
                SUM(CASE WHEN park_minutes >= 15 AND park_minutes < 30 THEN 1 ELSE 0 END) AS parked_15_to_30min,
                SUM(CASE WHEN park_minutes >= 30 AND park_minutes < 60 THEN 1 ELSE 0 END) AS parked_30min_to_1hr,
                SUM(CASE WHEN park_minutes >= 60 AND park_minutes < 120 THEN 1 ELSE 0 END) AS parked_1hr_to_2hr,
                SUM(CASE WHEN park_minutes >= 120 AND park_minutes < 180 THEN 1 ELSE 0 END) AS parked_2hr_to_3hr,
                SUM(CASE WHEN park_minutes >= 180 AND park_minutes < 240 THEN 1 ELSE 0 END) AS parked_3hr_to_4hr,
                SUM(CASE WHEN park_minutes >= 240 AND park_minutes < 300 THEN 1 ELSE 0 END) AS parked_4hr_to_5hr,
                SUM(CASE WHEN park_minutes >= 300 AND park_minutes < 360 THEN 1 ELSE 0 END) AS parked_5hr_to_6hr,
                SUM(CASE WHEN park_minutes >= 360 THEN 1 ELSE 0 END) AS parked_above_6hr,
                COUNT(*) AS total_vehicles
            FROM base
            WHERE (@normalizedCardTypeFilter IS NULL OR cardtype = @normalizedCardTypeFilter)
            GROUP BY exitdate, dayofweek, cardtype
            ORDER BY exitdate, cardtype;";

                var flat = (await conn.QueryAsync(sql, new { start, end, normalizedCardTypeFilter })).ToList();

                var grandTotalsAll = new Dictionary<string, int[]>();
                foreach (var ct in allCardTypes)
                {
                    var ctData = flat.Where(r => (string)r.cardtype == ct).ToList();
                    var totals = new int[10];
                    totals[0] = ctData.Sum(r => (int)r.parked_0_to_15min);
                    totals[1] = ctData.Sum(r => (int)r.parked_15_to_30min);
                    totals[2] = ctData.Sum(r => (int)r.parked_30min_to_1hr);
                    totals[3] = ctData.Sum(r => (int)r.parked_1hr_to_2hr);
                    totals[4] = ctData.Sum(r => (int)r.parked_2hr_to_3hr);
                    totals[5] = ctData.Sum(r => (int)r.parked_3hr_to_4hr);
                    totals[6] = ctData.Sum(r => (int)r.parked_4hr_to_5hr);
                    totals[7] = ctData.Sum(r => (int)r.parked_5hr_to_6hr);
                    totals[8] = ctData.Sum(r => (int)r.parked_above_6hr);
                    totals[9] = ctData.Sum(r => (int)r.total_vehicles);
                    grandTotalsAll[ct] = totals;
                }

                var cardTypes = allCardTypes.Where(ct => grandTotalsAll[ct][9] > 0).ToList();

                var dates = flat
                    .Select(r => new { date = (DateTime)r.exitdate, dayOfWeek = (string)r.dayofweek })
                    .Distinct()
                    .OrderBy(d => d.date)
                    .ToList();

                var wb = new NPOI.HSSF.UserModel.HSSFWorkbook();
                var sheet = wb.CreateSheet("Parking Duration Analysis");
                sheet.CreateFreezePane(2, 3);

                var dfmt = wb.CreateDataFormat();

                ICellStyle MakeStyle(bool bold = false, short bgColor = -1, bool right = false, bool center = false,
                    string? numFormat = null, short fontColor = -1)
                {
                    var st = wb.CreateCellStyle();
                    var ft = wb.CreateFont();
                    ft.IsBold = bold;
                    ft.FontHeightInPoints = 9;
                    if (fontColor >= 0) ft.Color = fontColor;
                    st.SetFont(ft);
                    if (bgColor >= 0)
                    {
                        st.FillForegroundColor = bgColor;
                        st.FillPattern = NPOI.SS.UserModel.FillPattern.SolidForeground;
                    }
                    st.Alignment = center ? NPOI.SS.UserModel.HorizontalAlignment.Center
                                          : right ? NPOI.SS.UserModel.HorizontalAlignment.Right
                                                  : NPOI.SS.UserModel.HorizontalAlignment.Left;
                    st.BorderTop = NPOI.SS.UserModel.BorderStyle.Thin;
                    st.BorderBottom = NPOI.SS.UserModel.BorderStyle.Thin;
                    st.BorderLeft = NPOI.SS.UserModel.BorderStyle.Thin;
                    st.BorderRight = NPOI.SS.UserModel.BorderStyle.Thin;
                    if (numFormat != null) st.DataFormat = dfmt.GetFormat(numFormat);
                    return st;
                }

                var hssfWb = (NPOI.HSSF.UserModel.HSSFWorkbook)wb;
                var customPalette = hssfWb.GetCustomPalette();
                customPalette.SetColorAtIndex(NPOI.HSSF.Util.HSSFColor.Rose.Index, 0xFC, 0xD5, 0xB5);
                short stPeachColorIndex = NPOI.HSSF.Util.HSSFColor.Rose.Index;

                var stTitle = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.DarkBlue.Index,
                    fontColor: NPOI.SS.UserModel.IndexedColors.White.Index, center: true);
                var stCardTypeHeader = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.LightBlue.Index, center: true);
                var stHeader = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Grey25Percent.Index, center: true);
                var stDate = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.LightCornflowerBlue.Index);
                var stData = MakeStyle(right: true, numFormat: "#,##0");
                var stGrandTotalHeader = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Yellow.Index, center: true);
                var stGrandTotal = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.Yellow.Index, right: true, numFormat: "#,##0");
                var stGrandTotalDataNoFill = MakeStyle(bold: true, right: true, numFormat: "#,##0");
                var stPercent = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.LightYellow.Index, right: true, numFormat: @"0.00""%""");
                var stPercentLabel = MakeStyle(bold: true, bgColor: NPOI.SS.UserModel.IndexedColors.LightYellow.Index, center: true);

                var stCardTotalHeader = MakeStyle(bold: true, bgColor: stPeachColorIndex, center: true);
                var stCardTotal = MakeStyle(bold: true, bgColor: stPeachColorIndex, right: true, numFormat: "#,##0");

                void S(IRow row, int col, string val, ICellStyle st)
                {
                    var c = row.CreateCell(col);
                    c.SetCellValue(val);
                    c.CellStyle = st;
                }

                void N(IRow row, int col, int val, ICellStyle st)
                {
                    var c = row.CreateCell(col);
                    c.SetCellValue(val);
                    c.CellStyle = st;
                }

                int rowIdx = 0;
                int totalCols = 2 + (cardTypes.Count * 10) + 10;

                var titleRow = sheet.CreateRow(rowIdx++);
                var titleText = string.IsNullOrWhiteSpace(normalizedCardTypeFilter)
                    ? $"Parking Duration Analysis ({start:dd-MMM-yyyy} to {end.AddDays(-1):dd-MMM-yyyy})"
                    : $"Parking Duration Analysis - {normalizedCardTypeFilter} ({start:dd-MMM-yyyy} to {end.AddDays(-1):dd-MMM-yyyy})";
                S(titleRow, 0, titleText, stTitle);
                sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(0, 0, 0, totalCols - 1));

                var cardTypeRow = sheet.CreateRow(rowIdx++);
                S(cardTypeRow, 0, "", stHeader);
                S(cardTypeRow, 1, "", stHeader);

                int col = 2;
                foreach (var ct in cardTypes)
                {
                    S(cardTypeRow, col, ct, stCardTypeHeader);
                    sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(rowIdx - 1, rowIdx - 1, col, col + 9));
                    for (int i = 1; i < 10; i++)
                    {
                        S(cardTypeRow, col + i, "", stCardTypeHeader);
                    }
                    col += 10;
                }

                S(cardTypeRow, col, "GRAND TOTAL", stGrandTotalHeader);
                sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(rowIdx - 1, rowIdx - 1, col, col + 9));
                for (int i = 1; i < 10; i++)
                {
                    S(cardTypeRow, col + i, "", stGrandTotalHeader);
                }

                var headerRow = sheet.CreateRow(rowIdx++);
                S(headerRow, 0, "DATE", stHeader);
                S(headerRow, 1, "DAY", stHeader);

                col = 2;
                foreach (var ct in cardTypes)
                {
                    S(headerRow, col++, "0-15min", stHeader);
                    S(headerRow, col++, "15-30min", stHeader);
                    S(headerRow, col++, "30-60min", stHeader);
                    S(headerRow, col++, "1Hr-2Hr", stHeader);
                    S(headerRow, col++, "2Hr-3Hr", stHeader);
                    S(headerRow, col++, "3Hr-4Hr", stHeader);
                    S(headerRow, col++, "4Hr-5Hr", stHeader);
                    S(headerRow, col++, "5Hr-6Hr", stHeader);
                    S(headerRow, col++, "Above 6Hr", stHeader);
                    S(headerRow, col++, "TOTAL", stCardTotalHeader);
                }

                S(headerRow, col++, "0-15min", stGrandTotalHeader);
                S(headerRow, col++, "15-30min", stGrandTotalHeader);
                S(headerRow, col++, "30-60min", stGrandTotalHeader);
                S(headerRow, col++, "1Hr-2Hr", stGrandTotalHeader);
                S(headerRow, col++, "2Hr-3Hr", stGrandTotalHeader);
                S(headerRow, col++, "3Hr-4Hr", stGrandTotalHeader);
                S(headerRow, col++, "4Hr-5Hr", stGrandTotalHeader);
                S(headerRow, col++, "5Hr-6Hr", stGrandTotalHeader);
                S(headerRow, col++, "Above 6Hr", stGrandTotalHeader);
                S(headerRow, col++, "TOTAL", stCardTotalHeader);

                sheet.SetColumnWidth(0, 4200);
                sheet.SetColumnWidth(1, 4000);
                for (int i = 2; i < totalCols; i++) sheet.SetColumnWidth(i, 3500);

                var grandTotals = new Dictionary<string, int[]>();
                foreach (var ct in cardTypes)
                {
                    grandTotals[ct] = new int[10];
                }

                int[] overallGrandTotal = new int[10];

                foreach (var dateInfo in dates)
                {
                    var dr = sheet.CreateRow(rowIdx++);
                    S(dr, 0, dateInfo.date.ToString("dd-MMM-yyyy"), stDate);
                    S(dr, 1, dateInfo.dayOfWeek, stDate);

                    col = 2;
                    int[] dayTotal = new int[10];

                    foreach (var ct in cardTypes)
                    {
                        var record = flat.FirstOrDefault(r =>
                            ((DateTime)r.exitdate).Date == dateInfo.date.Date &&
                            (string)r.cardtype == ct);

                        if (record != null)
                        {
                            N(dr, col++, (int)record.parked_0_to_15min, stData);
                            N(dr, col++, (int)record.parked_15_to_30min, stData);
                            N(dr, col++, (int)record.parked_30min_to_1hr, stData);
                            N(dr, col++, (int)record.parked_1hr_to_2hr, stData);
                            N(dr, col++, (int)record.parked_2hr_to_3hr, stData);
                            N(dr, col++, (int)record.parked_3hr_to_4hr, stData);
                            N(dr, col++, (int)record.parked_4hr_to_5hr, stData);
                            N(dr, col++, (int)record.parked_5hr_to_6hr, stData);
                            N(dr, col++, (int)record.parked_above_6hr, stData);
                            N(dr, col++, (int)record.total_vehicles, stCardTotal);

                            grandTotals[ct][0] += (int)record.parked_0_to_15min;
                            grandTotals[ct][1] += (int)record.parked_15_to_30min;
                            grandTotals[ct][2] += (int)record.parked_30min_to_1hr;
                            grandTotals[ct][3] += (int)record.parked_1hr_to_2hr;
                            grandTotals[ct][4] += (int)record.parked_2hr_to_3hr;
                            grandTotals[ct][5] += (int)record.parked_3hr_to_4hr;
                            grandTotals[ct][6] += (int)record.parked_4hr_to_5hr;
                            grandTotals[ct][7] += (int)record.parked_5hr_to_6hr;
                            grandTotals[ct][8] += (int)record.parked_above_6hr;
                            grandTotals[ct][9] += (int)record.total_vehicles;

                            dayTotal[0] += (int)record.parked_0_to_15min;
                            dayTotal[1] += (int)record.parked_15_to_30min;
                            dayTotal[2] += (int)record.parked_30min_to_1hr;
                            dayTotal[3] += (int)record.parked_1hr_to_2hr;
                            dayTotal[4] += (int)record.parked_2hr_to_3hr;
                            dayTotal[5] += (int)record.parked_3hr_to_4hr;
                            dayTotal[6] += (int)record.parked_4hr_to_5hr;
                            dayTotal[7] += (int)record.parked_5hr_to_6hr;
                            dayTotal[8] += (int)record.parked_above_6hr;
                            dayTotal[9] += (int)record.total_vehicles;
                        }
                        else
                        {
                            for (int i = 0; i < 9; i++)
                            {
                                N(dr, col++, 0, stData);
                            }
                            N(dr, col++, 0, stCardTotal);
                        }
                    }

                    for (int i = 0; i < 9; i++)
                    {
                        N(dr, col++, dayTotal[i], stGrandTotalDataNoFill);
                        overallGrandTotal[i] += dayTotal[i];
                    }
                    N(dr, col++, dayTotal[9], stCardTotal);
                    overallGrandTotal[9] += dayTotal[9];
                }

                var finalRow = sheet.CreateRow(rowIdx++);
                S(finalRow, 0, "GRAND TOTAL", stGrandTotalHeader);
                S(finalRow, 1, "", stGrandTotalHeader);

                col = 2;
                foreach (var ct in cardTypes)
                {
                    for (int i = 0; i < 9; i++)
                    {
                        N(finalRow, col++, grandTotals[ct][i], stGrandTotal);
                    }
                    N(finalRow, col++, grandTotals[ct][9], stCardTotal);
                }

                for (int i = 0; i < 9; i++)
                {
                    N(finalRow, col++, overallGrandTotal[i], stGrandTotal);
                }
                N(finalRow, col++, overallGrandTotal[9], stCardTotal);

                var percentRow = sheet.CreateRow(rowIdx++);
                S(percentRow, 0, "PERCENTAGE", stPercentLabel);
                S(percentRow, 1, "", stPercentLabel);

                col = 2;
                foreach (var ct in cardTypes)
                {
                    int total = grandTotals[ct][9];

                    if (total > 0)
                    {
                        for (int i = 0; i < 9; i++)
                        {
                            double percentage = ((double)grandTotals[ct][i] / total) * 100.0;
                            var c = percentRow.CreateCell(col++);
                            c.SetCellValue(percentage);
                            c.CellStyle = stPercent;
                        }

                        var totalCell = percentRow.CreateCell(col++);
                        totalCell.SetCellValue(100.0);
                        totalCell.CellStyle = stPercent;
                    }
                    else
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            var c = percentRow.CreateCell(col++);
                            c.SetCellValue(0.0);
                            c.CellStyle = stPercent;
                        }
                    }
                }

                int overallTotal = overallGrandTotal[9];
                if (overallTotal > 0)
                {
                    for (int i = 0; i < 9; i++)
                    {
                        double percentage = ((double)overallGrandTotal[i] / overallTotal) * 100.0;
                        var c = percentRow.CreateCell(col++);
                        c.SetCellValue(percentage);
                        c.CellStyle = stPercent;
                    }

                    var totalCell = percentRow.CreateCell(col++);
                    totalCell.SetCellValue(100.0);
                    totalCell.CellStyle = stPercent;
                }
                else
                {
                    for (int i = 0; i < 10; i++)
                    {
                        var c = percentRow.CreateCell(col++);
                        c.SetCellValue(0.0);
                        c.CellStyle = stPercent;
                    }
                }

                using var ms = new System.IO.MemoryStream();
                wb.Write(ms);

                var fileNameSuffix = string.IsNullOrWhiteSpace(normalizedCardTypeFilter)
                    ? ""
                    : $"_{normalizedCardTypeFilter.Replace(" ", "")}";
                string fileName = $"ParkingDurationAnalysis{fileNameSuffix}_{start:yyyyMMdd}_{end.AddDays(-1):yyyyMMdd}.xls";

                return File(ms.ToArray(), "application/vnd.ms-excel", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExportParkingDurationExcel failed");
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
} 