using AmaanParkingSystem.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace AmaanParkingSystem.Controllers
{
    public class DatabaseLoginController : Controller
    {
        private readonly IConnectionStringService _connStrService;
        private readonly IDatabaseSettingsService _dbSettingsService;
        private readonly ILogger<DatabaseLoginController> _logger;

        public const string DbSessionKey = "DbConnectionEstablished";

        public DatabaseLoginController(
            IConnectionStringService connStrService,
            IDatabaseSettingsService dbSettingsService,
            ILogger<DatabaseLoginController> logger)
        {
            _connStrService = connStrService;
            _dbSettingsService = dbSettingsService;
            _logger = logger;
        }

        // ─── GET /DatabaseLogin/Index ────────────────────────────────────────
        // Shown only when there are no saved credentials, OR when auto-connect
        // failed (middleware ran but could not open the connection).
        [HttpGet]
        public IActionResult Index()
        {
            // If the middleware already rebuilt the connection string successfully,
            // skip this page entirely and go straight to the operator login page.
            if (_connStrService.IsConfigured
                && !string.IsNullOrEmpty(HttpContext.Session.GetString(DbSessionKey)))
            {
                return RedirectToAction("Login", "Utility");
            }

            // Pre-fill saved settings if they exist (auto-connect failed scenario).
            var saved = _dbSettingsService.Load();
            if (saved != null)
            {
                ViewBag.ServerName = saved.ServerName;
                ViewBag.DatabaseName = saved.DatabaseName;
                ViewBag.Username = saved.Username;
                ViewBag.Password = DatabaseSettingsService.DecryptPassword(saved.EncryptedPassword);
                ViewBag.RememberMe = true;
                // Tell the view why we are here — the saved credentials failed.
                ViewBag.AutoConnectFailed = true;
            }

            return View();
        }

        // ─── GET /DatabaseLogin/AutoConnect ─────────────────────────────────
        // Kept for compatibility (used by any existing AJAX callers).
        // Returns JSON; the startup middleware is the primary auto-connect path.
        [HttpGet]
        public IActionResult AutoConnect()
        {
            // Already connected in this session — nothing to do.
            if (_connStrService.IsConfigured
                && !string.IsNullOrEmpty(HttpContext.Session.GetString(DbSessionKey)))
            {
                return Json(new { success = true });
            }

            var saved = _dbSettingsService.Load();
            if (saved == null)
            {
                return Json(new { success = false, message = "No saved database settings found." });
            }

            try
            {
                string decryptedPassword =
                    DatabaseSettingsService.DecryptPassword(saved.EncryptedPassword);

                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = saved.ServerName,
                    InitialCatalog = saved.DatabaseName,
                    UserID = saved.Username,
                    Password = decryptedPassword,
                    TrustServerCertificate = true,
                    MultipleActiveResultSets = true,
                    ConnectTimeout = 15
                };

                using var conn = new SqlConnection(builder.ConnectionString);
                conn.Open();
                conn.Close();

                _connStrService.SetConnectionString(builder.ConnectionString);
                HttpContext.Session.SetString(DbSessionKey, "true");

                _logger.LogInformation(
                    "AutoConnect endpoint succeeded. Server={Server}, Database={Database}",
                    saved.ServerName, saved.DatabaseName);

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AutoConnect endpoint failed.");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ─── POST /DatabaseLogin/Index ───────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Index(
            string serverName,
            string databaseName,
            string username,
            string password,
            bool rememberMe = false)
        {
            if (string.IsNullOrWhiteSpace(serverName)
             || string.IsNullOrWhiteSpace(databaseName)
             || string.IsNullOrWhiteSpace(username)
             || string.IsNullOrWhiteSpace(password))
            {
                ViewBag.Error = "All fields are required.";
                return View();
            }

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = serverName.Trim(),
                InitialCatalog = databaseName.Trim(),
                UserID = username.Trim(),
                Password = password,
                TrustServerCertificate = true,
                MultipleActiveResultSets = true,
                ConnectTimeout = 15
            };

            string candidateConnectionString = builder.ConnectionString;

            try
            {
                using var conn = new SqlConnection(candidateConnectionString);
                conn.Open();
                conn.Close();

                _connStrService.SetConnectionString(candidateConnectionString);

                // Always save when the user successfully connects manually so that
                // the next restart can auto-connect without asking again.
                // If rememberMe is unchecked, skip saving (existing behaviour).
                if (rememberMe)
                {
                    _dbSettingsService.Save(
                        serverName.Trim(), databaseName.Trim(), username.Trim(), password);

                    _logger.LogInformation(
                        "DB settings saved. Server={Server}, Database={Database}, User={User}",
                        serverName, databaseName, username);
                }

                HttpContext.Session.SetString(DbSessionKey, "true");
                return RedirectToAction("Login", "Utility");
            }
            catch (SqlException ex)
            {
                _logger.LogWarning(ex,
                    "Database connection failed. Server={Server}, Database={Database}, User={User}",
                    serverName, databaseName, username);

                ViewBag.Error = $"Connection failed: {ex.Message}";
                ViewBag.ServerName = serverName;
                ViewBag.DatabaseName = databaseName;
                ViewBag.Username = username;
                ViewBag.Password = password;
                ViewBag.RememberMe = rememberMe;
                return View();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during database connection attempt.");

                ViewBag.Error = $"Unexpected error: {ex.Message}";
                ViewBag.ServerName = serverName;
                ViewBag.DatabaseName = databaseName;
                ViewBag.Username = username;
                ViewBag.Password = password;
                ViewBag.RememberMe = rememberMe;
                return View();
            }
        }
    }
}