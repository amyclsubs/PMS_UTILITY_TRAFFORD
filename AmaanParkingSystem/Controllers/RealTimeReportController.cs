using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;
using Microsoft.Extensions.Logging;
using AmaanParkingSystem.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Controllers
{
    public class RealTimeReportController : BaseController
    {
        private readonly ILogger<RealTimeReportController> _logger;

        public RealTimeReportController(ILogger<RealTimeReportController> logger)
        {
            _logger = logger;
        }

        // ─── VIEW: All roles can view the real-time report ───────────────────
        [HttpGet]
        public IActionResult Index()
        {
            _logger.LogInformation("RealTimeReport Index loaded");
            return View("~/Views/Reports/RealTimeReport.cshtml");
        }

        /// <summary>
        /// Current Entries = Cars that entered TODAY and are still parked (action_status = 'Entered').
        /// All roles can view.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> GetCurrentEntries()
        {
            _logger.LogInformation("GetCurrentEntries called");

            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());

                string sql = @"
                    SELECT
                          [id],
                          [customer_id],
                          [cardno],
                          [card_name],
                          [card_type],
                          [car_plate_no],
                          [entry_time],
                          [fee_schedule_name],
                          [action_status],
                          [charges],
                          [charge_status],
                          [remark],
                          [balance_to_collect],
                          [pay_mode],
                          [paid_amount],
                          [transaction_status],
                          [isValidated],
                          [current_charges],
                          [real_time_charges],
                          [car_parkers_company_id],
                          [car_parkers_sub_co_id],
                          [car_parkers_sub_co_name],
                          [mpesa_receipt_number],
                          [park_time_untill_validation],
                          [validation_time]
                    FROM dbo.[db_tbl_14_car_enterd]
                    WHERE
                        CAST([entry_time] AS DATE) = CAST(GETDATE() AS DATE)
                        AND [action_status] = 'Entered'
                    ORDER BY [entry_time] DESC;";

                var rows = (await conn.QueryAsync<RealTimeEntryRow>(sql)).ToList();

                foreach (var row in rows)
                {
                    if (row.entry_time.HasValue)
                    {
                        row.entry_date = row.entry_time.Value.ToString("dd-MMM-yyyy");
                        row.entry_time_only = row.entry_time.Value.ToString("HH:mm:ss");
                        row.park_time_live = FormatDuration(DateTime.Now - row.entry_time.Value);
                    }
                    row.display_charges = row.real_time_charges ?? row.current_charges ?? row.charges ?? 0;
                    var vi = GetValidationInfo(row.isValidated);
                    row.validation_label = vi.label;
                    row.validation_color = vi.color;
                }

                _logger.LogInformation("GetCurrentEntries rows found: {Count}", rows.Count);
                return Json(new { success = true, count = rows.Count, debugMessage = $"Today's parked vehicles: {rows.Count} records", data = rows });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetCurrentEntries failed");
                return Json(new { success = false, count = 0, debugMessage = ex.Message, data = new List<RealTimeEntryRow>() });
            }
        }

        /// <summary>
        /// Past Entries = Cars that entered BEFORE TODAY and are still parked (action_status = 'Entered').
        /// All roles can view.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> GetPastEntries()
        {
            _logger.LogInformation("GetPastEntries called");

            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());

                string sql = @"
                    SELECT
                          [id],
                          [customer_id],
                          [cardno],
                          [card_name],
                          [card_type],
                          [car_plate_no],
                          [entry_time],
                          [fee_schedule_name],
                          [action_status],
                          [charges],
                          [charge_status],
                          [remark],
                          [balance_to_collect],
                          [pay_mode],
                          [paid_amount],
                          [transaction_status],
                          [isValidated],
                          [current_charges],
                          [real_time_charges],
                          [car_parkers_company_id],
                          [car_parkers_sub_co_id],
                          [car_parkers_sub_co_name],
                          [mpesa_receipt_number],
                          [park_time_untill_validation],
                          [validation_time]
                    FROM dbo.[db_tbl_14_car_enterd]
                    WHERE
                        CAST([entry_time] AS DATE) < CAST(GETDATE() AS DATE)
                        AND [action_status] = 'Entered'
                    ORDER BY [entry_time] ASC;";

                var rows = (await conn.QueryAsync<RealTimeEntryRow>(sql)).ToList();

                foreach (var row in rows)
                {
                    if (row.entry_time.HasValue)
                    {
                        row.entry_date = row.entry_time.Value.ToString("dd-MMM-yyyy");
                        row.entry_time_only = row.entry_time.Value.ToString("HH:mm:ss");
                        row.park_time_live = FormatDuration(DateTime.Now - row.entry_time.Value);
                    }

                    row.display_charges = row.real_time_charges ?? row.current_charges ?? row.charges ?? 0;

                    // Validation status:
                    // 0 / NULL = Not Validated
                    // 1        = Validated
                    // 10       = Revalidation Required
                    // 11       = Revalidated
                    var vi = GetValidationInfo(row.isValidated);
                    row.validation_label = vi.label;
                    row.validation_color = vi.color;
                }

                _logger.LogInformation("GetPastEntries rows found: {Count}", rows.Count);
                return Json(new { success = true, count = rows.Count, debugMessage = $"Overdue (still parked from previous days): {rows.Count} records", data = rows });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetPastEntries failed");
                return Json(new { success = false, count = 0, debugMessage = ex.Message, data = new List<RealTimeEntryRow>() });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCardTypes()
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                var sql = @"
            SELECT DISTINCT LTRIM(RTRIM([card_type])) AS card_type
            FROM [AMAAN_PMS].[dbo].[db_tbl_10_cards_master]
            WHERE ISNULL(LTRIM(RTRIM([card_type])), '') <> ''
            ORDER BY LTRIM(RTRIM([card_type]));";

                var cardTypes = (await conn.QueryAsync<string>(sql)).ToList();
                return Json(new { success = true, data = cardTypes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetCardTypes failed");
                return Json(new { success = false, data = new List<string>(), message = ex.Message });
            }
        }

        // ── GET: /RealTimeReport/GetB2PendingCars ────────────────────────────
        // Duplicates B2EntryNotificationService.GetPendingB2EntriesAsync() logic
        // directly here so Operators (who cannot access B2NotificationController)
        // can still see the data on the Real Time Report page.
        [HttpGet]
        public async Task<IActionResult> GetB2PendingCars()
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());

                // ThresholdMinutes default = 15 (same as service default)
                const int thresholdMinutes = 15;

                const string sql = @"
            SELECT
                [id],
                [cardno],
                [card_name],
                [card_type],
                [car_plate_no],
                [entry_time],
                [car_parkers_sub_co_id],
                [car_parkers_sub_co_name],
                [fee_schedule_name]
            FROM dbo.[db_tbl_14_car_enterd]
            WHERE [action_status] = 'Entered'
              AND [card_type] IN ('DAILY_ACCESS', 'SEASONAL')
              AND [B2_entry_time] IS NULL
              AND [entry_time] IS NOT NULL
              AND DATEDIFF(MINUTE, [entry_time], GETDATE()) >= @thresholdMinutes
            ORDER BY [entry_time] ASC;";

                var rows = (await conn.QueryAsync<dynamic>(sql, new { thresholdMinutes })).ToList();

                var data = rows.Select(r => new
                {
                    id = (int)r.id,
                    cardno = (string)r.cardno,
                    card_name = (string)r.card_name,
                    card_type = (string)r.card_type,
                    car_plate_no = (string)r.car_plate_no,
                    entry_time = ((DateTime?)r.entry_time)?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "—",
                    minutes_parked = r.entry_time != null
                                                ? (int)(DateTime.Now - (DateTime)r.entry_time).TotalMinutes : 0,
                    car_parkers_sub_co_id = (string)r.car_parkers_sub_co_id,
                    car_parkers_sub_co_name = (string)r.car_parkers_sub_co_name,
                    fee_schedule_name = (string)r.fee_schedule_name
                }).ToList();

                return Json(new { success = true, count = data.Count, data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetB2PendingCars failed");
                return Json(new { success = false, count = 0, data = new List<object>(), message = ex.Message });
            }
        }

        // GET: RealTimeReport/GetB2NotifiedCards
        // Shows DB-based B2 notification status for Real Time Report B2 tab
        [HttpGet]
        public async Task<IActionResult> GetB2NotifiedCards()
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());

                const string sql = @"
            SELECT
                  EntryId
                , CardNo
                , CardName
                , CardType
                , CarPlateNo
                , CompanyId
                , CompanyName
                , Email
                , EntryTime
                , NotificationTime
                , Status
            FROM dbo.db_tbl_28_B2Notificationlog
            ORDER BY NotificationTime DESC;";

                var rows = (await conn.QueryAsync(sql)).ToList();

                var cooldowns = rows.Select(r =>
                {
                    int entryId = (int)(r.EntryId ?? 0);
                    string cardNo = (string)(r.CardNo ?? "");
                    string cardName = (string)(r.CardName ?? "");
                    string cardType = (string)(r.CardType ?? "");
                    string companyName = (string)(r.CompanyName ?? "");
                    string email = (string)(r.Email ?? "");
                    DateTime? nt = r.NotificationTime as DateTime?;
                    DateTime? et = r.EntryTime as DateTime?;
                    string status = (string)(r.Status ?? "Email Sent");

                    return new
                    {
                        // keep same name used in B2 view JS: "cardno|entryId"
                        subCoId = $"{cardNo}|{entryId}",
                        cardNo = cardNo,
                        cardName = cardName,
                        cardType = cardType,
                        subCompanyName = companyName,
                        email = email,
                        entryTime = et?.ToString("dd-MMM-yyyy HH:mm:ss"),
                        notificationTime = nt?.ToString("dd-MMM-yyyy HH:mm:ss"),
                        status = status,
                        lastNotified = nt?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "",
                        minutesAgo = nt == null ? 0.0 : (DateTime.Now - nt.Value).TotalMinutes,
                        onCooldown = true
                    };
                }).ToList();

                // service status numbers from table (optional aggregates)
                int totalSent = rows.Count(r => ((string)(r.Status ?? "")).Contains("Sent"));
                int totalFailed = rows.Count(r => ((string)(r.Status ?? "")).Contains("Fail"));

                return Json(new
                {
                    success = true,
                    isEnabled = true, // view-only; service enable flag is not needed here
                    lastRunTime = rows.Any()
                        ? ((DateTime?)rows.First().NotificationTime)?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "Never"
                        : "Never",
                    nextRunTime = "",               // not used on this page
                    lastRunStatus = "",             // not used on this page
                    totalEmailsSent = totalSent,
                    totalFailed = totalFailed,
                    cooldowns = cooldowns
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetB2NotifiedCards failed");
                return Json(new
                {
                    success = false,
                    isEnabled = false,
                    cooldowns = new object[0],
                    message = ex.Message
                });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetDeletedCards(string fromDate, string toDate)
        {
            try
            {
                // Parse dates – default to today if missing
                if (!DateTime.TryParse(fromDate, out DateTime from))
                    from = DateTime.Today;
                if (!DateTime.TryParse(toDate, out DateTime to))
                    to = DateTime.Today;

                // Ensure 'to' covers the full end day (23:59:59)
                to = to.Date.AddDays(1).AddSeconds(-1);

                using var conn = new SqlConnection(GetDynamicConnectionString());

                string sql = @"
            SELECT
                [archive_id],
                [id],
                [cardno],
                [card_name],
                [card_type],
                [car_plate_no],
                [entry_time],
                [fee_schedule_name],
                [action_status],
                [charges],
                [charge_status],
                [balance_to_collect],
                [pay_mode],
                [paid_amount],
                [transaction_status],
                [isValidated],
                [current_charges],
                [real_time_charges],
                [car_parkers_sub_co_id],
                [car_parkers_sub_co_name],
                [mpesa_receipt_number],
                [remark],
                [removed_at],
                [removed_by]
            FROM dbo.[db_tbl_27_Cards_notexited]
            WHERE [removed_at] >= @from AND [removed_at] <= @to
            ORDER BY [removed_at] DESC;";

                var rows = (await conn.QueryAsync<DeletedCardRow>(sql, new { from, to })).ToList();

                var data = rows.Select(r => new
                {
                    r.archive_id,
                    r.id,
                    r.cardno,
                    r.card_name,
                    r.card_type,
                    r.car_plate_no,
                    entry_time = r.entry_time?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "—",
                    r.fee_schedule_name,
                    r.action_status,
                    r.charges,
                    r.charge_status,
                    r.balance_to_collect,
                    r.pay_mode,
                    r.paid_amount,
                    r.transaction_status,
                    r.isValidated,
                    r.current_charges,
                    r.real_time_charges,
                    r.car_parkers_sub_co_id,
                    r.car_parkers_sub_co_name,
                    r.mpesa_receipt_number,
                    r.remark,
                    removed_at = r.removed_at?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "—",
                    r.removed_by,
                    display_charges = r.real_time_charges ?? r.current_charges ?? r.charges ?? 0
                }).ToList();

                return Json(new { success = true, count = data.Count, data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDeletedCards failed");
                return Json(new { success = false, count = 0, data = new List<object>(), message = ex.Message });
            }
        }

        /// <summary>
        /// Removes a single overdue entry from db_tbl_14_car_enterd and
        /// inserts it into db_tbl_27_Cards_notexited with a mandatory remark
        /// and the login ID of the user who performed the removal.
        ///
        /// RBAC: MANAGER and ADMIN only. OPERATOR is blocked.
        /// VALIDATION: remark is mandatory — empty remark is rejected.
        /// AUDIT: removed_by is set to the logged-in operator's login ID.
        /// </summary>
        [HttpPost]
        [RoleAuthorize("ADMIN", "MANAGER")]
        public async Task<IActionResult> RemoveOverdueEntry(int id, string remark)
        {
            _logger.LogInformation("RemoveOverdueEntry called for id={Id}", id);

            // ── Validation ────────────────────────────────────────────────────
            if (id <= 0)
                return Json(new { success = false, message = "Invalid entry ID." });

            if (string.IsNullOrWhiteSpace(remark))
                return Json(new { success = false, message = "Remark is required. Please enter a reason before removing this entry." });

            // ── Audit: who is doing this removal ─────────────────────────────
            string removedBy = GetLoggedInUser();

            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                using var transaction = conn.BeginTransaction();
                try
                {
                    // 1. Fetch the row first
                    string selectSql = @"
                        SELECT TOP 1 * FROM dbo.[db_tbl_14_car_enterd]
                        WHERE [id] = @id AND [action_status] = 'Entered';";
                    var row = await conn.QueryFirstOrDefaultAsync<RealTimeEntryRow>(selectSql, new { id }, transaction);

                    if (row == null)
                    {
                        await transaction.RollbackAsync();
                        return Json(new { success = false, message = "Entry not found or already exited." });
                    }

                    // 2. Insert into archive table — includes removed_by (login ID) and remark
                    string insertSql = @"
                        INSERT INTO dbo.[db_tbl_27_Cards_notexited]
                        (
                            [id], [customer_id], [cardno], [card_name], [card_type], [car_plate_no],
                            [entry_time], [fee_schedule_name], [action_status], [charges], [charge_status],
                            [balance_to_collect], [pay_mode], [paid_amount], [transaction_status],
                            [isValidated], [current_charges], [real_time_charges],
                            [car_parkers_company_id], [car_parkers_sub_co_id], [car_parkers_sub_co_name],
                            [mpesa_receipt_number], [park_time_untill_validation], [validation_time],
                            [remark], [removed_at], [removed_by]
                        )
                        VALUES
                        (
                            @id, @customer_id, @cardno, @card_name, @card_type, @car_plate_no,
                            @entry_time, @fee_schedule_name, @action_status, @charges, @charge_status,
                            @balance_to_collect, @pay_mode, @paid_amount, @transaction_status,
                            @isValidated, @current_charges, @real_time_charges,
                            @car_parkers_company_id, @car_parkers_sub_co_id, @car_parkers_sub_co_name,
                            @mpesa_receipt_number, @park_time_untill_validation, @validation_time,
                            @remark, GETDATE(), @removed_by
                        );";

                    await conn.ExecuteAsync(insertSql, new
                    {
                        row.id,
                        row.customer_id,
                        row.cardno,
                        row.card_name,
                        row.card_type,
                        row.car_plate_no,
                        row.entry_time,
                        row.fee_schedule_name,
                        row.action_status,
                        row.charges,
                        row.charge_status,
                        row.balance_to_collect,
                        row.pay_mode,
                        row.paid_amount,
                        row.transaction_status,
                        row.isValidated,
                        row.current_charges,
                        row.real_time_charges,
                        row.car_parkers_company_id,
                        row.car_parkers_sub_co_id,
                        row.car_parkers_sub_co_name,
                        row.mpesa_receipt_number,
                        row.park_time_untill_validation,
                        row.validation_time,
                        remark = remark.Trim(),
                        removed_by = removedBy
                    }, transaction);

                    // 3. Delete from entry table
                    string deleteSql = "DELETE FROM dbo.[db_tbl_14_car_enterd] WHERE [id] = @id;";
                    await conn.ExecuteAsync(deleteSql, new { id }, transaction);

                    await transaction.CommitAsync();

                    _logger.LogInformation("RemoveOverdueEntry success for id={Id} by user={User}", id, removedBy);
                    return Json(new { success = true, message = "Entry removed and archived successfully." });
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RemoveOverdueEntry failed for id={Id}", id);
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        [RoleAuthorize("ADMIN", "MANAGER")]
        public async Task<IActionResult> ExitOverdueTag(
            int id,
            string exitTime,
            decimal charge)
        {
            _logger.LogInformation(
                "ExitOverdueTag called. ID={Id}, ExitTime={ExitTime}, Charge={Charge}",
                id,
                exitTime,
                charge
            );

            if (id <= 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Invalid entry ID."
                });
            }

            if (!DateTime.TryParse(exitTime, out DateTime requestedExitTime))
            {
                return Json(new
                {
                    success = false,
                    message = "Invalid exit time."
                });
            }

            if (charge < 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Exit charge cannot be negative."
                });
            }

            try
            {
                using var conn =
                    new SqlConnection(GetDynamicConnectionString());

                await conn.OpenAsync();

                using var transaction = conn.BeginTransaction();

                try
                {
                    // =========================================================
                    // 1. GET ORIGINAL ENTRY
                    // =========================================================

                    const string entrySql = @"
SELECT TOP 1
       [id],
       [customer_id],
       [cardno],
       [card_name],
       [card_type],
       [car_plate_no],
       [entry_time],
       [entry_lane_name],
       [fee_schedule_id],
       [fee_schedule_name],
       [park_time_untill_validation],
       [park_time_additional],
       [addn_chgs],
       [validation_time],
       [revalidation_time],
       [action_status],
       [charges],
       [charge_status],
       [discount_token_no],
       [discount_amount],
       [reason],
       [remark],
       [balance_to_collect],
       [payment_source],
       [pay_mode],
       [paid_amount],
       [transaction_id],
       [transaction_status],
       [isValidated],
       [created_by],
       [created_on],
       [updated_by],
       [updated_on],
       [current_charges],
       [pay_source],
       [revalidation_charges],
       [car_parkers_company_id],
       [car_parkers_sub_co_id],
       [car_parkers_sub_co_name],
       [B2_entry_time],
       [B2_exit_time],
       [B1_entry_time],
       [B1_exit_time],
       [real_time_charges],
       [mpesa_receipt_number],
       [tag_current_charges]
FROM [AMAAN_PMS].[dbo].[db_tbl_14_car_enterd]
WHERE [id] = @id
  AND [action_status] = 'Entered';";

                    var entry =
                        await conn.QueryFirstOrDefaultAsync<dynamic>(
                            entrySql,
                            new { id },
                            transaction
                        );

                    if (entry == null)
                    {
                        await transaction.RollbackAsync();

                        return Json(new
                        {
                            success = false,
                            message = "Entry not found or already exited."
                        });
                    }

                    // =========================================================
                    // 2. ENTRY TIME
                    // =========================================================

                    if (entry.entry_time == null)
                    {
                        await transaction.RollbackAsync();

                        return Json(new
                        {
                            success = false,
                            message = "Entry time is missing."
                        });
                    }

                    DateTime entryTime =
                        Convert.ToDateTime(entry.entry_time);

                    if (requestedExitTime < entryTime)
                    {
                        await transaction.RollbackAsync();

                        return Json(new
                        {
                            success = false,
                            message =
                                "Exit time cannot be earlier than entry time."
                        });
                    }

                    // =========================================================
                    // 3. TEMPORARY CARD CHECK
                    // =========================================================

                    string cardType =
                        Convert.ToString(entry.card_type)?.Trim() ?? "";

                    if (cardType.Equals(
                            "TEMPORARY",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await transaction.RollbackAsync();

                        return Json(new
                        {
                            success = false,
                            message =
                                "Temporary cards must be removed using Remove."
                        });
                    }

                    // =========================================================
                    // 4. GET SUB COMPANY
                    // =========================================================

                    string subCompanyId =
                        Convert.ToString(
                            entry.car_parkers_sub_co_id
                        )?.Trim() ?? "";

                    if (string.IsNullOrWhiteSpace(subCompanyId))
                    {
                        await transaction.RollbackAsync();

                        return Json(new
                        {
                            success = false,
                            message =
                                "Sub Company ID is missing. Cannot process exit."
                        });
                    }

                    // =========================================================
                    // 5. READ BALANCE ONLY
                    //
                    // IMPORTANT:
                    // Balance_Amount IS NEVER UPDATED HERE.
                    // =========================================================

                    const string companySql = @"
SELECT TOP 1
       [id],
       ISNULL([Balance_Amount], 0) AS [Balance_Amount],
       ISNULL([Neg_Balance_allowed], 0) AS [Neg_Balance_allowed]
FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
WHERE [car_parkers_sub_co_id] = @subCompanyId
  AND [car_parkers_status] = '1'
ORDER BY [id];";

                    var company =
                        await conn.QueryFirstOrDefaultAsync<dynamic>(
                            companySql,
                            new
                            {
                                subCompanyId
                            },
                            transaction
                        );

                    if (company == null)
                    {
                        await transaction.RollbackAsync();

                        return Json(new
                        {
                            success = false,
                            message =
                                "Sub Company not found or inactive."
                        });
                    }

                    decimal currentBalance =
                        Convert.ToDecimal(company.Balance_Amount);

                    bool negativeBalanceAllowed =
                        Convert.ToInt32(
                            company.Neg_Balance_allowed
                        ) == 1;

                    // =========================================================
                    // 6. CHECK SUFFICIENT BALANCE
                    //
                    // The manually entered charge is used here.
                    // =========================================================

                    if (charge > 0 &&
                        !negativeBalanceAllowed &&
                        currentBalance < charge)
                    {
                        await transaction.RollbackAsync();

                        return Json(new
                        {
                            success = false,
                            message =
                                $"No sufficient balance to exit. " +
                                $"Required: KES {charge:N2}, " +
                                $"Available: KES {currentBalance:N2}."
                        });
                    }

                    // =========================================================
                    // 7. PARKING TIME
                    // =========================================================

                    TimeSpan parkDuration =
                        requestedExitTime - entryTime;

                    // db_tbl_15_car_exited.park_time_total is INT.
                    // Store total parking duration in minutes.
                    int parkTimeTotal =
                        Math.Max(
                            0,
                            (int)Math.Floor(
                                parkDuration.TotalMinutes
                            )
                        );

                    int chargeableMinutes =
                        parkTimeTotal;

                    // =========================================================
                    // 8. INSERT INTO TAG LOG
                    //
                    // This is the ONLY balance-driving entry.
                    // DO NOT UPDATE Balance_Amount.
                    // =========================================================

                    const string tagLogSql = @"
INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_21_TAG_log]
(
    [cardno],
    [company_id],
    [car_parkers_sub_co_id],
    [car_parkers_sub_co_name],
    [car_plate_no],
    [card_name],
    [card_type],
    [entry_time],
    [exit_time],
    [B2_entry_time],
    [B2_exit_time],
    [diff_entry_minutes],
    [diff_exit_minutes],
    [daily_charge],
    [penalty_charge],
    [total_charge],
    [is_free_slot],
    [created_on],
    [remarks],
    [B1_entry_time],
    [real_time_charges],
    [B1_exit_time]
)
VALUES
(
    @cardno,
    @companyId,
    @subCompanyId,
    @subCompanyName,
    @plate,
    @cardName,
    @cardType,
    @entryTime,
    @exitTime,
    @B2EntryTime,
    @B2ExitTime,
    @diffEntryMinutes,
    @diffExitMinutes,
    @charge,
    0,
    @charge,
    0,
    GETDATE(),
    'Manual overdue exit from Real Time Report',
    @B1EntryTime,
    @charge,
    @B1ExitTime
);";

                    await conn.ExecuteAsync(
                        tagLogSql,
                        new
                        {
                            cardno =
                                Convert.ToString(entry.cardno),

                            companyId =
                                Convert.ToString(
                                    entry.car_parkers_company_id),

                            subCompanyId =
                                Convert.ToString(
                                    entry.car_parkers_sub_co_id),

                            subCompanyName =
                                Convert.ToString(
                                    entry.car_parkers_sub_co_name),

                            plate =
                                Convert.ToString(
                                    entry.car_plate_no),

                            cardName =
                                Convert.ToString(
                                    entry.card_name),

                            cardType =
                                Convert.ToString(
                                    entry.card_type),

                            entryTime = entryTime,

                            exitTime = requestedExitTime,

                            B2EntryTime = (DateTime?)null,

                            B2ExitTime = (DateTime?)null,

                            diffEntryMinutes = 0,

                            diffExitMinutes = 0,

                            charge = charge,

                            B1EntryTime = (DateTime?)null,

                            B1ExitTime = (DateTime?)null
                        },
                        transaction
                    );

                    // =========================================================
                    // 9. INSERT INTO EXIT TABLE
                    //
                    // IMPORTANT:
                    // The following fields DO NOT exist in the Entry table:
                    // free_minutes
                    // namaz_exempted_minutes
                    // additional_charges
                    // exit_lane_name
                    //
                    // Therefore we use 0 / NULL here.
                    // =========================================================

                    const string insertExitSql = @"
INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_15_car_exited]
(
    [customer_id],
    [cardno],
    [card_name],
    [card_type],
    [car_plate_no],
    [entry_time],
    [fee_schedule_id],
    [fee_schedule_name],
    [park_time_untill_validation],
    [park_time_additional],
    [addn_chgs],
    [validation_time],
    [revalidation_time],
    [action_status],
    [charges],
    [charge_status],
    [discount_token_no],
    [discount_amount],
    [reason],
    [remark],
    [balance_to_collect],
    [payment_source],
    [pay_mode],
    [paid_amount],
    [transaction_id],
    [transaction_status],
    [isValidated],
    [exit_time],
    [park_time_total],
    [created_by],
    [created_on],
    [updated_by],
    [updated_on],
    [total_fee],
    [chargeable_minutes],
    [free_minutes],
    [namaz_exempted_minutes],
    [additional_charges],
    [pay_source],
    [B1_entry_time],
    [B1_exit_time],
    [B2_entry_time],
    [B2_exit_time],
    [car_parkers_company_id],
    [car_parkers_sub_co_id],
    [car_parkers_sub_co_name],
    [exit_lane_name],
    [entry_lane_name]
)
SELECT
    [customer_id],
    [cardno],
    [card_name],
    [card_type],
    [car_plate_no],
    [entry_time],
    [fee_schedule_id],
    [fee_schedule_name],
    [park_time_untill_validation],
    [park_time_additional],
    [addn_chgs],
    [validation_time],
    [revalidation_time],
    'Exited',
    @charge,
    'Paid',
    [discount_token_no],
    [discount_amount],
    [reason],
    'Exit completed from Real Time Report',
    0,
    'Balance',
    'Balance',
    @charge,
    [transaction_id],
    'Success',
    [isValidated],
    @exitTime,
    @parkTimeTotal,
    [created_by],
    [created_on],
    [updated_by],
    GETDATE(),
    @charge,
    @chargeableMinutes,
    0,
    0,
    0,
  'Balance',
NULL,
NULL,
NULL,
NULL,
[car_parkers_company_id],
    [car_parkers_sub_co_id],
    [car_parkers_sub_co_name],
    NULL,
    [entry_lane_name]
FROM [AMAAN_PMS].[dbo].[db_tbl_14_car_enterd]
WHERE [id] = @id
  AND [action_status] = 'Entered';";

                    int inserted =
                        await conn.ExecuteAsync(
                            insertExitSql,
                            new
                            {
                                id,
                                charge,
                                exitTime = requestedExitTime,
                                parkTimeTotal,
                                chargeableMinutes
                            },
                            transaction
                        );

                    if (inserted != 1)
                    {
                        await transaction.RollbackAsync();

                        return Json(new
                        {
                            success = false,
                            message =
                                "Unable to create exit record."
                        });
                    }

                    // =========================================================
                    // 10. DELETE ORIGINAL ACTIVE ENTRY
                    // =========================================================

                    const string deleteSql = @"
DELETE FROM [AMAAN_PMS].[dbo].[db_tbl_14_car_enterd]
WHERE [id] = @id
  AND [action_status] = 'Entered';";

                    int deleted =
                        await conn.ExecuteAsync(
                            deleteSql,
                            new { id },
                            transaction
                        );

                    if (deleted != 1)
                    {
                        await transaction.RollbackAsync();

                        return Json(new
                        {
                            success = false,
                            message =
                                "Original entry could not be removed. " +
                                "Transaction rolled back."
                        });
                    }

                    // =========================================================
                    // 11. COMMIT
                    // =========================================================

                    await transaction.CommitAsync();

                    _logger.LogInformation(
                        "ExitOverdueTag successful. ID={Id}, Charge={Charge}",
                        id,
                        charge
                    );

                    return Json(new
                    {
                        success = true,
                        message = "Vehicle exited successfully.",
                        charge = charge.ToString("F2"),
                        exitTime =
                            requestedExitTime.ToString(
                                "dd-MMM-yyyy HH:mm:ss"
                            )
                    });
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "ExitOverdueTag failed for ID={Id}",
                    id
                );

                return Json(new
                {
                    success = false,
                    message = "Exit failed: " + ex.Message
                });
            }
        }


        private static (string label, string color) GetValidationInfo(int? v)
        {
            switch (v)
            {
                case 1: return ("Validated", "#198754"); // green
                case 10: return ("Revalidation Required", "#dc3545"); // red
                case 11: return ("Revalidated", "#0d6efd"); // blue
                default: return ("Not Validated", "#6c757d"); // grey (0 / NULL)
            }
        }

        private string FormatDuration(TimeSpan span)
        {
            if (span.TotalSeconds < 0) return "00:00:00";
            return string.Format(
                "{0:D2}:{1:D2}:{2:D2}",
                (int)span.TotalHours,
                span.Minutes,
                span.Seconds
            );
        }
    }

    public class RealTimeEntryRow
    {
        public int id { get; set; }
        public string customer_id { get; set; }
        public string cardno { get; set; }
        public string card_name { get; set; }
        public string card_type { get; set; }
        public string car_plate_no { get; set; }
        public DateTime? entry_time { get; set; }
        public string fee_schedule_name { get; set; }
        public string action_status { get; set; }
        public decimal? charges { get; set; }
        public string charge_status { get; set; }
        public string remark { get; set; }
        public decimal? balance_to_collect { get; set; }
        public string pay_mode { get; set; }
        public decimal? paid_amount { get; set; }
        public string transaction_status { get; set; }
        public int? isValidated { get; set; }
        public decimal? current_charges { get; set; }
        public decimal? real_time_charges { get; set; }
        public string car_parkers_company_id { get; set; }
        public string car_parkers_sub_co_id { get; set; }
        public string car_parkers_sub_co_name { get; set; }
        public string mpesa_receipt_number { get; set; }
        public string park_time_untill_validation { get; set; }
        public DateTime? validation_time { get; set; }

        // Computed fields (not in DB)
        public string entry_date { get; set; }
        public string entry_time_only { get; set; }
        public string park_time_live { get; set; }
        public decimal display_charges { get; set; }
        public string validation_label { get; set; }
        public string validation_color { get; set; }
    }
    public class DeletedCardRow
    {
        public int archive_id { get; set; }
        public int id { get; set; }
        public string cardno { get; set; }
        public string card_name { get; set; }
        public string card_type { get; set; }
        public string car_plate_no { get; set; }
        public DateTime? entry_time { get; set; }
        public string fee_schedule_name { get; set; }
        public string action_status { get; set; }
        public decimal? charges { get; set; }
        public string charge_status { get; set; }
        public decimal? balance_to_collect { get; set; }
        public string pay_mode { get; set; }
        public decimal? paid_amount { get; set; }
        public string transaction_status { get; set; }
        public int? isValidated { get; set; }
        public decimal? current_charges { get; set; }
        public decimal? real_time_charges { get; set; }
        public string car_parkers_sub_co_id { get; set; }
        public string car_parkers_sub_co_name { get; set; }
        public string mpesa_receipt_number { get; set; }
        public string remark { get; set; }
        public DateTime? removed_at { get; set; }
        public string removed_by { get; set; }
    }
}