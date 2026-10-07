using AmaanParkingSystem.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Controllers
{
    public class B2NotificationController : BaseController
    {
        private readonly B2EntryNotificationService _b2Service;
        private readonly ILogger<B2NotificationController> _logger;

        public B2NotificationController(
            B2EntryNotificationService b2Service,
            ILogger<B2NotificationController> logger)
        {
            _b2Service = b2Service;
            _logger = logger;
        }

        // GET: /B2Notification
        public IActionResult Index()
        {
            return View();
        }

        // ── GET: /B2Notification/GetStatus ──────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetStatus()
        {
            try
            {
                // read full log list from service (DB)
                var logRows = await _b2Service.GetNotificationLogAsync();

                var cooldowns = logRows
                    .Select(row =>
                    {
                        // dynamic from Dapper
                        int entryId = (int)(row.EntryId ?? 0);
                        string cardNo = (string)(row.CardNo ?? "");
                        string cardName = (string)(row.CardName ?? "");
                        string cardType = (string)(row.CardType ?? "");
                        string companyName = (string)(row.CompanyName ?? "");
                        string email = (string)(row.Email ?? "");
                        DateTime? nt = row.NotificationTime as DateTime?;
                        DateTime? et = row.EntryTime as DateTime?;
                        string status = (string)(row.Status ?? "Email Sent");

                        return new
                        {
                            // keep same property name used in JS: subCoId = "cardno|entryId"
                            subCoId = $"{cardNo}|{entryId}",
                            cardNo = cardNo,
                            cardName = cardName,
                            cardType = cardType,
                            subCompanyName = companyName,
                            email = email,
                            entryTime = et?.ToString("dd-MMM-yyyy HH:mm:ss"),
                            notificationTime = nt?.ToString("dd-MMM-yyyy HH:mm:ss"),
                            status = status,
                            lastNotified = nt?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "Already notified",
                            minutesAgo = nt == null ? 0.0 : (DateTime.Now - nt.Value).TotalMinutes,
                            onCooldown = true
                        };
                    })
                    .ToList();

                return Json(new
                {
                    success = true,
                    isEnabled = _b2Service.IsEnabled,
                    thresholdMinutes = _b2Service.ThresholdMinutes,
                    cooldownMinutes = _b2Service.CooldownMinutes,
                    intervalMinutes = _b2Service.IntervalMinutes,
                    lastRunTime = _b2Service.LastRunTime?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "Never",
                    nextRunTime = _b2Service.NextRunTime?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "—",
                    lastRunStatus = _b2Service.LastRunStatus,
                    lastPendingCount = _b2Service.LastPendingCount,
                    totalEmailsSent = _b2Service.TotalEmailsSent,
                    totalFailed = _b2Service.TotalEmailsFailed,
                    cooldowns = cooldowns
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"B2 GetStatus Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ── POST: /B2Notification/SetEnabled ────────────────────────────────
        [HttpPost]
        public IActionResult SetEnabled([FromBody] SetEnabledRequest request)
        {
            try
            {
                _b2Service.IsEnabled = request.Enable;
                string msg = request.Enable
                    ? "✅ B2 Notification Service ENABLED"
                    : "⏸️ B2 Notification Service DISABLED";
                _logger.LogInformation(msg);
                return Json(new { success = true, message = msg, enabled = request.Enable });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ── POST: /B2Notification/UpdateSettings ────────────────────────────
        [HttpPost]
        public IActionResult UpdateSettings([FromBody] UpdateSettingsRequest request)
        {
            try
            {
                if (request.ThresholdMinutes < 1 || request.ThresholdMinutes > 1440)
                    return Json(new { success = false, message = "Threshold must be between 1 and 1440 minutes." });

                if (request.CooldownMinutes < 1 || request.CooldownMinutes > 1440)
                    return Json(new { success = false, message = "Cooldown must be between 1 and 1440 minutes." });

                _b2Service.ThresholdMinutes = request.ThresholdMinutes;
                _b2Service.CooldownMinutes = request.CooldownMinutes;

                _logger.LogInformation(
                    $"[B2 Notify] Settings updated — Threshold: {request.ThresholdMinutes} min, Cooldown: {request.CooldownMinutes} min");

                return Json(new
                {
                    success = true,
                    message = $"✅ Settings saved — Threshold: {request.ThresholdMinutes} min, Cooldown: {request.CooldownMinutes} min",
                    thresholdMinutes = _b2Service.ThresholdMinutes,
                    cooldownMinutes = _b2Service.CooldownMinutes
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ── POST: /B2Notification/TriggerNow ────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> TriggerNow()
        {
            try
            {
                _logger.LogInformation("[B2 Notify] Manual trigger via UI");
                await _b2Service.TriggerManualAsync();
                return Json(new
                {
                    success = true,
                    message = "✅ Manual check triggered. Check status for results.",
                    lastRunStatus = _b2Service.LastRunStatus
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ── POST: /B2Notification/ClearCooldowns ────────────────────────────
        [HttpPost]
        public IActionResult ClearCooldowns()
        {
            try
            {
                _b2Service.ClearCooldowns();
                return Json(new
                {
                    success = true,
                    message = "✅ All cooldowns cleared. Next check will re-notify all pending companies."
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ── GET: /B2Notification/GetPendingCars ─────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetPendingCars()
        {
            try
            {
                var pending = await _b2Service.GetPendingB2EntriesAsync();

                var data = pending.Select(p => new
                {
                    id = p.id,
                    cardno = p.cardno,
                    card_name = p.card_name,
                    card_type = p.card_type,
                    car_plate_no = p.car_plate_no,
                    entry_time = p.entry_time?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "—",
                    minutes_parked = p.entry_time.HasValue
                                            ? (int)(DateTime.Now - p.entry_time.Value).TotalMinutes : 0,
                    car_parkers_sub_co_id = p.car_parkers_sub_co_id,
                    car_parkers_sub_co_name = p.car_parkers_sub_co_name,
                    fee_schedule_name = p.fee_schedule_name
                }).ToList();

                return Json(new { success = true, data, count = data.Count });
            }
            catch (Exception ex)
            {
                _logger.LogError($"B2 GetPendingCars Error: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ── Request Models ───────────────────────────────────────────────────
        public class SetEnabledRequest
        {
            public bool Enable { get; set; }
        }

        public class UpdateSettingsRequest
        {
            public int ThresholdMinutes { get; set; }
            public int CooldownMinutes { get; set; }
        }
    }
}