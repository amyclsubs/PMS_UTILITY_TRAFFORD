using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Services
{
    /// <summary>
    /// B2EntryNotificationService — Background service that checks every 5 minutes.
    ///
    /// LOGIC:
    ///   1. Find all DAILY_ACCESS and SEASONAL cards currently parked (action_status = 'Entered')
    ///      that have been inside for >= ThresholdMinutes.
    ///   2. If B2_entry_time IS NOT NULL → car has already moved to B2 → skip, do nothing.
    ///   3. If B2_entry_time IS NULL     → car has not yet moved to B2.
    ///      → Look up sub company email from db_tbl_08_parkers_company using car_parkers_sub_co_id.
    ///      → Send one email per sub company (grouped) reminding them to move to B2.
    ///      → Skip sub companies that already received a notification within CooldownMinutes
    ///        (tracked now in db_tbl_28_B2Notificationlog).
    ///
    /// Register in Program.cs:
    ///   builder.Services.AddSingleton<B2EntryNotificationService>();
    ///   builder.Services.AddHostedService(sp => sp.GetRequiredService<B2EntryNotificationService>());
    /// </summary>
    public class B2EntryNotificationService : BackgroundService
    {
        private readonly ILogger<B2EntryNotificationService> _logger;
        private readonly IConnectionStringService _connStrService;
        private readonly IConfiguration _configuration;
        private Timer _timer;

        // ── Runtime-configurable settings (in-memory, reset on restart) ─────
        public bool IsEnabled { get; set; } = true;
        public int ThresholdMinutes { get; set; } = 15;
        public int CooldownMinutes { get; set; } = 60;
        public int IntervalMinutes { get; set; } = 5;

        // ── Status tracking (read by controller for UI) ──────────────────────
        public DateTime? LastRunTime { get; private set; }
        public DateTime? NextRunTime { get; private set; }
        public int TotalEmailsSent { get; private set; } = 0;
        public int TotalEmailsFailed { get; private set; } = 0;
        public string LastRunStatus { get; private set; } = "Disabled on startup";
        public int LastPendingCount { get; private set; } = 0;

        // 🧪 TRIAL MODE: true = run every 1 minute for testing, false = production
        private const bool TRIAL_MODE = false;

        // NOTE: in‑memory HashSet is no longer used for cooldown; DB is used instead.
        // These members stay only to keep public API shape for now.
        private readonly HashSet<string> _notifiedEntryKeys
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string EntryKey(B2PendingEntry e)
            => $"{e.cardno}_{e.id}";

        public B2EntryNotificationService(
            ILogger<B2EntryNotificationService> logger,
            IConnectionStringService connStrService,
            IConfiguration configuration)
        {
            _logger = logger;
            _connStrService = connStrService;
            _configuration = configuration;
        }

        private string GetConnectionString()
        {
            return _connStrService.GetConnectionString()
                   ?? _configuration.GetConnectionString("DefaultConnection")
                   ?? throw new InvalidOperationException("No connection string found.");
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (TRIAL_MODE)
            {
                _logger.LogInformation("🧪 ============================================");
                _logger.LogInformation("🧪 B2 Entry Notification — TRIAL MODE (every 1 min)");
                _logger.LogInformation("🧪 ============================================");
                _timer = new Timer(
                    async (state) => await DoWorkAsync(state),
                    null,
                    TimeSpan.Zero,
                    TimeSpan.FromMinutes(1));
            }
            else
            {
                _logger.LogInformation("⏸️ B2 Entry Notification Service STARTED IN DISABLED MODE — enable from UI to activate");
                _timer = new Timer(
                    async (state) => await DoWorkAsync(state),
                    null,
                    TimeSpan.Zero,
                    TimeSpan.FromMinutes(IntervalMinutes));
            }

            NextRunTime = DateTime.Now.AddMinutes(TRIAL_MODE ? 1 : IntervalMinutes);
            return Task.CompletedTask;
        }

        /// <summary>Called by controller for instant manual trigger from UI.</summary>
        public async Task TriggerManualAsync()
        {
            _logger.LogInformation("[B2 Notify] 🖱 Manual trigger from UI");
            await DoWorkAsync(null);
        }

        /// <summary>Clear cooldowns: now clears DB log instead of only memory.</summary>
        public async void ClearCooldowns()
        {
            try
            {
                await ClearNotificationLogAsync();
                _notifiedEntryKeys.Clear(); // kept for backwards compatibility
                _logger.LogInformation("[B2 Notify] 🔄 Notification log cleared by admin");
            }
            catch (Exception ex)
            {
                _logger.LogError("[B2 Notify] ❌ Error clearing notification log from service: {Message}", ex.Message);
            }
        }

        /// <summary>Returns current cooldown state for UI display — now from DB.</summary>
        public List<string> GetNotifiedCardNos()
        {
            // Kept only so code compiles; controller will switch to GetNotificationLogAsync.
            return _notifiedEntryKeys.ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // MAIN WORK
        // ─────────────────────────────────────────────────────────────────────
        private async Task DoWorkAsync(object state)
        {
            LastRunTime = DateTime.Now;
            NextRunTime = DateTime.Now.AddMinutes(TRIAL_MODE ? 1 : IntervalMinutes);

            if (!IsEnabled)
            {
                _logger.LogInformation("[B2 Notify] ⏸ Service is DISABLED — skipping check");
                LastRunStatus = "Disabled";
                return;
            }

            try
            {
                _logger.LogInformation($"[B2 Notify] ⏱ Check at {DateTime.Now:HH:mm:ss}");

                // ── Step 1: Fetch qualifying entries ────────────────────────────────
                var entries = await GetPendingB2EntriesAsync();
                LastPendingCount = entries.Count;

                if (!entries.Any())
                {
                    _logger.LogInformation("[B2 Notify] ✅ No pending B2 notifications needed.");
                    LastRunStatus = $"OK — 0 pending at {DateTime.Now:HH:mm:ss}";
                    return;
                }

                _logger.LogInformation($"[B2 Notify] 🚗 Found {entries.Count} car(s) needing B2 notification");

                // ── Step 2: Filter out already-notified cards (DB cooldown) ────────
                var newEntries = new List<B2PendingEntry>();
                int alreadyNotified = 0;

                foreach (var e in entries)
                {
                    var hasRecent = await HasRecentNotificationAsync(e.id);
                    if (hasRecent)
                    {
                        alreadyNotified++;
                        continue;
                    }
                    newEntries.Add(e);
                }

                if (alreadyNotified > 0)
                    _logger.LogInformation("[B2 Notify] ⏭ Skipping {Count} car(s) — already notified (DB cooldown)", alreadyNotified);

                if (!newEntries.Any())
                {
                    _logger.LogInformation("[B2 Notify] ✅ All pending cars already notified (DB cooldown).");
                    LastRunStatus = $"OK — all notified at {DateTime.Now:HH:mm:ss}";
                    return;
                }

                // ── Step 3: Get SMTP settings ────────────────────────────────────────
                var smtp = await GetSmtpSettingsAsync();

                // ── Step 4: Send one email per car ─────────────────────────────────
                int sent = 0, skipped = 0, failed = 0;

                foreach (var car in newEntries)
                {
                    string subCoId = car.car_parkers_sub_co_id ?? "UNKNOWN";

                    // Look up sub company email
                    var subCo = await GetSubCompanyAsync(subCoId);

                    if (subCo == null || string.IsNullOrWhiteSpace(subCo.Email))
                    {
                        _logger.LogWarning("[B2 Notify] ⚠️ No email for sub co {SubCoId} (card: {CardNo}) — skipping",
                            subCoId, car.cardno);
                        skipped++;
                        await InsertNotificationLogAsync(car, subCo, false, "Skipped: no email configured");
                        continue;
                    }

                    string subject = $"🅿️ Action Required: Please Move Your Vehicle to B2 Parking – {car.car_plate_no ?? car.cardno}";
                    string body = BuildEmailBody(subCo, new System.Collections.Generic.List<B2PendingEntry> { car });

                    bool ok = await SendEmailAsync(smtp, subCo.Email, subject, body);

                    if (ok)
                    {
                        sent++;
                        TotalEmailsSent++;
                        _notifiedEntryKeys.Add(EntryKey(car)); // no longer used for cooldown, but kept
                        _logger.LogInformation(
                            "[B2 Notify] ✅ Sent to {SubCo} ({Email}) for card {CardNo} / plate {Plate}",
                            subCo.SubCoName, subCo.Email, car.cardno, car.car_plate_no);

                        await InsertNotificationLogAsync(car, subCo, true, "Email sent successfully");
                    }
                    else
                    {
                        failed++;
                        TotalEmailsFailed++;
                        _logger.LogWarning("[B2 Notify] ❌ Failed for card {CardNo} — {SubCo} ({Email})",
                            car.cardno, subCo.SubCoName, subCo.Email);

                        await InsertNotificationLogAsync(car, subCo, false, "SMTP send failed");
                    }
                }

                _logger.LogInformation("[B2 Notify] Summary: Sent={Sent}, Skipped={Skipped}, Failed={Failed}",
                    sent, skipped, failed);

                LastRunStatus = $"OK — Sent {sent}, Skipped {skipped}, Failed {failed} at {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                _logger.LogError("[B2 Notify] ❌ Error in DoWorkAsync: {Message}", ex.Message);
                LastRunStatus = $"Error: {ex.Message}";
                return;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // DB helpers for notification log
        // ─────────────────────────────────────────────────────────────────────
        private async Task<bool> HasRecentNotificationAsync(int entryId)
        {
            try
            {
                using var conn = new SqlConnection(GetConnectionString());

                const string sql = @"
                    SELECT TOP 1 NotificationTime
                    FROM dbo.db_tbl_28_B2Notificationlog
                    WHERE EntryId = @EntryId
                    ORDER BY NotificationTime DESC;";

                var last = await conn.QueryFirstOrDefaultAsync<DateTime?>(sql, new { EntryId = entryId });
                if (last == null) return false;

                return (DateTime.Now - last.Value).TotalMinutes < CooldownMinutes;
            }
            catch (Exception ex)
            {
                _logger.LogError("[B2 Notify] ❌ Error checking recent notification for entry {EntryId}: {Message}",
                    entryId, ex.Message);
                return false;
            }
        }

        private async Task InsertNotificationLogAsync(B2PendingEntry car, SubCoInfo subCo, bool success, string remarks)
        {
            try
            {
                using var conn = new SqlConnection(GetConnectionString());

                const string sql = @"
                    INSERT INTO dbo.db_tbl_28_B2Notificationlog
                    (
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
                        , Remarks
                    )
                    VALUES
                    (
                          @EntryId
                        , @CardNo
                        , @CardName
                        , @CardType
                        , @CarPlateNo
                        , @CompanyId
                        , @CompanyName
                        , @Email
                        , @EntryTime
                        , GETDATE()
                        , @Status
                        , @Remarks
                    );";

                await conn.ExecuteAsync(sql, new
                {
                    EntryId = car.id,
                    CardNo = car.cardno,
                    CardName = car.card_name,
                    CardType = car.card_type,
                    CarPlateNo = car.car_plate_no,
                    CompanyId = car.car_parkers_company_id,
                    CompanyName = car.car_parkers_sub_co_name,
                    Email = subCo?.Email,
                    EntryTime = car.entry_time,
                    Status = success ? "Email Sent" : "Failed",
                    Remarks = remarks
                });
            }
            catch (Exception ex)
            {
                _logger.LogError("[B2 Notify] ❌ Error inserting notification log for entry {EntryId}: {Message}",
                    car.id, ex.Message);
            }
        }

        public async Task ClearNotificationLogAsync()
        {
            using var conn = new SqlConnection(GetConnectionString());
            const string sql = @"TRUNCATE TABLE dbo.db_tbl_28_B2Notificationlog;";
            await conn.ExecuteAsync(sql);
        }

        public async Task<IList<dynamic>> GetNotificationLogAsync()
        {
            try
            {
                using var conn = new SqlConnection(GetConnectionString());

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

                var rows = await conn.QueryAsync(sql);
                return rows.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError("[B2 Notify] ❌ Error fetching notification log: {Message}", ex.Message);
                return new List<dynamic>();
            }
        }

        // ═════════════════════════════════════════════════════════════════════==
        // DATA FETCHING
        // ═════════════════════════════════════════════════════════════════════==
        /// <summary>
        /// Returns all DAILY_ACCESS and SEASONAL cards that:
        ///   - Are currently parked (action_status = 'Entered')
        ///   - Have been parked for >= ThresholdMinutes
        ///   - Have NOT yet moved to B2 (B2_entry_time IS NULL)
        /// </summary>
        public async Task<List<B2PendingEntry>> GetPendingB2EntriesAsync()
        {
            try
            {
                var cs = GetConnectionString();

                using var conn = new SqlConnection(cs);

                const string sql = @"
                    SELECT
                        [id],
                        [cardno],
                        [card_name],
                        [card_type],
                        [car_plate_no],
                        [entry_time],
                        [B2_entry_time],
                        [action_status],
                        [car_parkers_sub_co_id],
                        [car_parkers_sub_co_name],
                        [car_parkers_company_id],
                        [fee_schedule_name]
                    FROM dbo.[db_tbl_14_car_enterd]
                    WHERE [action_status] = 'Entered'
                      AND [card_type] IN ('DAILY_ACCESS', 'SEASONAL')
                      AND [B2_entry_time] IS NULL
                      AND [entry_time] IS NOT NULL
                      AND DATEDIFF(MINUTE, [entry_time], GETDATE()) >= @ThresholdMinutes
                    ORDER BY [entry_time] ASC;";

                var rows = (await conn.QueryAsync<B2PendingEntry>(
                    sql, new { ThresholdMinutes })).ToList();

                return rows;
            }
            catch (Exception ex)
            {
                _logger.LogError("[B2 Notify] ❌ Error fetching pending B2 entries: {Message}", ex.Message);
                return new List<B2PendingEntry>();
            }
        }

        private async Task<SubCoInfo> GetSubCompanyAsync(string subCoId)
        {
            try
            {
                var cs = GetConnectionString();

                using var conn = new SqlConnection(cs);

                const string sql = @"
                    SELECT TOP 1
                        [id]                                AS Id,
                        [car_parkers_sub_co_id]             AS SubCoId,
                        [car_parkers_sub_co_name]           AS SubCoName,
                        [sub_co_car_parkers_contact_person] AS ContactPerson,
                        [sub_co_car_parkers_email]          AS Email,
                        [sub_co_car_parkers_contact_number] AS Phone
                    FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
                    WHERE CAST([car_parkers_sub_co_id] AS NVARCHAR(50)) = @SubCoId
                      AND [car_parkers_status] = 1";

                return await conn.QueryFirstOrDefaultAsync<SubCoInfo>(sql, new { SubCoId = subCoId });
            }
            catch (Exception ex)
            {
                _logger.LogError("[B2 Notify] ❌ Error fetching sub company {SubCoId}: {Message}", subCoId, ex.Message);
                return null;
            }
        }

        // ═════════════════════════════════════════════════════════════════════==
        // EMAIL
        // ═════════════════════════════════════════════════════════════════════==
        private async Task<(string server, int port, string username, string password,
                             string fromEmail, string fromName, bool ssl)> GetSmtpSettingsAsync()
        {
            var cs = GetConnectionString();

            using var conn = new SqlConnection(cs);

            var config = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT TOP 1 smtp_server, smtp_port, smtp_username, smtp_password,
                               from_email, from_name, enable_ssl
                  FROM [AMAAN_PMS].[dbo].[db_tbl_22_email_config]
                  WHERE is_active = 1
                  ORDER BY id DESC");

            if (config == null)
                throw new Exception("No active SMTP configuration found.");

            return (Convert.ToString(config.smtp_server),
                    Convert.ToInt32(config.smtp_port),
                    Convert.ToString(config.smtp_username),
                    Convert.ToString(config.smtp_password),
                    Convert.ToString(config.from_email),
                    Convert.ToString(config.from_name),
                    Convert.ToBoolean(config.enable_ssl));
        }

        private async Task<bool> SendEmailAsync(
            (string server, int port, string username, string password,
             string fromEmail, string fromName, bool ssl) smtp,
            string toEmail, string subject, string body)
        {
            try
            {
                using var client = new SmtpClient(smtp.server, smtp.port)
                {
                    Credentials = new NetworkCredential(smtp.username, smtp.password),
                    EnableSsl = smtp.ssl,
                    Timeout = 30000
                };

                using var message = new MailMessage
                {
                    From = new MailAddress(smtp.fromEmail, smtp.fromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = true
                };

                message.To.Add(toEmail);
                await client.SendMailAsync(message);
                await LogToEmailTableAsync(toEmail, subject, "B2-Notification", "Sent", null);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("[B2 Notify] ❌ SMTP error: {Message}", ex.Message);
                await LogToEmailTableAsync(toEmail, subject, "B2-Notification", "Failed", ex.Message);
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════════==
        // EMAIL BODY
        // ═════════════════════════════════════════════════════════════════════==
        private string BuildEmailBody(SubCoInfo subCo, List<B2PendingEntry> cars)
        {
            var carRows = string.Join("", cars.Select(c =>
            {
                double minsParked = c.entry_time.HasValue
                    ? (DateTime.Now - c.entry_time.Value).TotalMinutes : 0;
                string duration = minsParked >= 60
                    ? $"{(int)(minsParked / 60)}h {(int)(minsParked % 60)}m"
                    : $"{(int)minsParked}m";
                string entryTimeStr = c.entry_time.HasValue
                    ? c.entry_time.Value.ToString("dd-MMM-yyyy HH:mm:ss") : "—";

                return $@"
                <tr>
                 <td style='padding:8px 12px;border-bottom:1px solid #eee;'>{c.card_name ?? "—"}</td>
                 <td style='padding:8px 12px;border-bottom:1px solid #eee;'>{c.car_plate_no ?? "—"}</td>
                 <td style='padding:8px 12px;border-bottom:1px solid #eee;'>{c.card_type ?? "—"}</td>
                 <td style='padding:8px 12px;border-bottom:1px solid #eee;'>{entryTimeStr}</td>
                 <td style='padding:8px 12px;border-bottom:1px solid #eee;font-weight:bold;color:#c0392b;'>{duration}</td>
               </tr>";
            }));

            return $@"<!DOCTYPE html>
<html>
<head><meta charset='utf-8'>
<style>
  body{{margin:0;padding:0;background:#f5f5f5;font-family:Arial,sans-serif;}}
  .wrap{{max-width:680px;margin:30px auto;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 2px 12px rgba(0,0,0,0.1);}}
  .header{{background:#1a3c5e;color:#fff;padding:28px 32px;text-align:center;}}
  .header h1{{margin:0;font-size:22px;}}
  .header p{{margin:6px 0 0;font-size:13px;color:#a8c6e0;}}
  .body{{padding:28px 32px;}}
  .alert-box{{background:#fff3cd;border:1px solid #ffc107;border-radius:6px;padding:14px 18px;margin-bottom:20px;}}
  .alert-box strong{{color:#856404;}}
  table{{width:100%;border-collapse:collapse;margin-top:16px;}}
  thead th{{background:#1a3c5e;color:#fff;padding:10px 12px;text-align:left;font-size:13px;}}
  tbody tr:nth-child(even){{background:#f9f9f9;}}
  .footer{{background:#f0f0f0;text-align:center;font-size:11px;color:#999;padding:16px;}}
</style>
</head>
<body>
  <div class='wrap'>
    <div class='header'>
      <h1>🅿️ Amaan Parking Management System</h1>
      <p>B2 Parking — Move Vehicle Reminder</p>
    </div>
    <div class='body'>
      <p>Dear <strong>{subCo.ContactPerson ?? subCo.SubCoName ?? "Valued Client"}</strong>,</p>
      <div class='alert-box'>
        <strong>⚠️ Action Required:</strong> The following vehicle(s) under your company
        (<strong>{subCo.SubCoName}</strong>) have been parked for more than
        <strong>{ThresholdMinutes} minutes</strong> without proceeding to <strong>B2 Parking</strong>.
        Please ensure they are moved to B2 immediately.
      </div>
      <table>
        <thead>
          <tr>
             <th>Card Name</th><th>Plate No</th>
             <th>Card Type</th><th>Entry Time</th><th>Time Parked</th>
         </tr>
        </thead>
        <tbody>{carRows}</tbody>
      </table>
      <p style='margin-top:20px;font-size:13px;color:#555;'>
        <strong>Note:</strong> No further action is needed once the vehicle has scanned at the B2 entry gate.
      </p>
      <p style='margin-top:20px;'>Best regards,<br><strong>Amaan Parking Management System</strong></p>
    </div>
    <div class='footer'>
      Automated notification sent at {DateTime.Now:dd-MMM-yyyy HH:mm:ss}.<br>
      © {DateTime.Now.Year} Amaan Parking Management System. All rights reserved.
    </div>
  </div>
</body>
</html>";
        }

        public override void Dispose()
        {
            _timer?.Dispose();
            _logger.LogInformation("🛑 B2 Entry Notification Service STOPPED {Mode}",
                TRIAL_MODE ? "(TRIAL MODE)" : "");
            base.Dispose();
        }
        private async Task LogToEmailTableAsync(string recipientEmail, string subject, string emailType, string status, string errorMessage)
        {
            try
            {
                var cs = GetConnectionString();
                using var conn = new SqlConnection(cs);
                await conn.ExecuteAsync(
                    @"INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_29_email_logs]
                        (EmailType, RecipientEmail, CCEmail, Subject, CompanyId, CompanyName, SiteId, SiteName, Status, ErrorMessage, CreatedOn)
                      VALUES
                        (@EmailType, @RecipientEmail, NULL, @Subject, NULL, NULL, NULL, NULL, @Status, @ErrorMessage, GETDATE())",
                    new { EmailType = emailType, RecipientEmail = recipientEmail, Subject = subject, Status = status, ErrorMessage = errorMessage });
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[B2 Notify] LogToEmailTableAsync failed (non-fatal): {ex.Message}");
            }
        }
    }

    public class B2PendingEntry
    {
        public int id { get; set; }
        public string cardno { get; set; }
        public string card_name { get; set; }
        public string card_type { get; set; }
        public string car_plate_no { get; set; }
        public DateTime? entry_time { get; set; }
        public DateTime? B2_entry_time { get; set; }
        public string action_status { get; set; }
        public string car_parkers_sub_co_id { get; set; }
        public string car_parkers_sub_co_name { get; set; }
        public string car_parkers_company_id { get; set; }
        public string fee_schedule_name { get; set; }
    }

    public class SubCoInfo
    {
        public int Id { get; set; }
        public string SubCoId { get; set; }
        public string SubCoName { get; set; }
        public string ContactPerson { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
    }
}