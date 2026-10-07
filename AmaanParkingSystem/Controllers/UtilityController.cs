using AmaanParkingSystem.Controllers;
using AmaanParkingSystem.Filters;
using AmaanParkingSystem.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.SqlClient;
using System.Data;

public class UtilityController : BaseController
{
    private readonly IConnectionStringService _connStrService;
    private readonly CollectionService _collectionService;
    private readonly ILogger<UtilityController> _logger;
    private const string AuthSessionKey = "UtilityUser";

    public UtilityController(
        IConnectionStringService connStrService,
        CollectionService collectionService,
        ILogger<UtilityController> logger)
    {
        _connStrService = connStrService;
        _collectionService = collectionService;
        _logger = logger;
    }

    // ─── Session Guard ─────────────────────────────────────────────────────────
    // Overrides BaseController.OnActionExecuting to also guard UtilityUser session.
    // NOTE: Do NOT remove base.OnActionExecuting — it sets ViewBag role variables.
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var action = context.ActionDescriptor.RouteValues["action"];
        if (!string.Equals(action, "Login", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(HttpContext.Session.GetString(AuthSessionKey)))
        {
            context.Result = RedirectToAction("Login", "Utility");
            return;
        }
        base.OnActionExecuting(context);
    }

    // ─── Login ─────────────────────────────────────────────────────────────────
    [HttpGet]
    public IActionResult Login()
    {
        return View();
    }

    /// <summary>
    /// Authenticates the operator and stores operator_type in session.
    /// RBAC depends on "OperatorType" being set here.
    /// </summary>
    [HttpPost]
    public IActionResult Login(string loginId, string password, bool rememberMe)
    {
        if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(password))
        {
            ViewBag.LoginError = "Please enter both username and password.";
            return View();
        }

        try
        {
            var connectionString = _connStrService.GetConnectionString();

            using (SqlConnection con = new SqlConnection(connectionString))
            {
                string sql = @"
                SELECT TOP 1 
                    operator_login_id,
                    operator_type
                FROM [AMAAN_PMS].[dbo].[db_tbl_01_operator]
                WHERE operator_login_id = @loginId
                AND (operator_password = @password OR DEVELOPER_PASSWORD = @password)";

                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@loginId", loginId);
                    cmd.Parameters.AddWithValue("@password", password);

                    con.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            string operatorLogin = reader["operator_login_id"].ToString();
                            string operatorType = reader["operator_type"].ToString()
                                                       .Trim()
                                                       .ToUpperInvariant();

                            // ── Store both user identity AND role in session ──────────
                            HttpContext.Session.SetString("UtilityUser", operatorLogin);
                            HttpContext.Session.SetString("OperatorType", operatorType);
                            // Also store under standard keys used by GetLoggedInUser()
                            HttpContext.Session.SetString("Username", operatorLogin);
                            // ────────────────────────────────────────────────────────

                            _logger.LogInformation(
                                $"User '{operatorLogin}' logged in as '{operatorType}'");

                            return RedirectToAction("Index", "Utility");
                        }
                        else
                        {
                            ViewBag.LoginError = "Invalid username or password.";
                            return View();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login error");
            ViewBag.LoginError = "An error occurred during login.";
            return View();
        }
    }

    // ─── Dashboard ────────────────────────────────────────────────────────────
    // All three roles can view the dashboard — no restriction here.
    public IActionResult Index()
    {
        _logger.LogInformation("Dashboard Index page loaded");
        return View();
    }

    // ─── Dashboard Data Endpoints (read-only — all roles allowed) ────────────
    [HttpPost]
    public async Task<IActionResult> GetDashboardMetrics([FromBody] DateRangeRequest request)
    {
        try
        {
            _logger.LogInformation($"GetDashboardMetrics called: {request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd}");

            if (request.StartDate == default || request.EndDate == default)
            {
                _logger.LogWarning("Invalid date range received");
                return Json(new { success = false, message = "Invalid date range" });
            }

            var stats = new object();

            using (SqlConnection con = new SqlConnection(GetDynamicConnectionString()))
            {
                string sql = @"
                SELECT
                    COUNT(*) as TotalVehicles,
                    ISNULL(SUM(CAST([charges] AS DECIMAL(18,2))), 0) as TotalCharges,
                    ISNULL(SUM(CASE 
                        WHEN UPPER(REPLACE(REPLACE(ISNULL([pay_mode],''), ' ', ''), '-', '')) = 'CASH' 
                        THEN CAST([paid_amount] AS DECIMAL(18,2)) ELSE 0 
                    END), 0) as TotalCashCollected,
                    ISNULL(SUM(CASE 
                        WHEN (UPPER(REPLACE(REPLACE(ISNULL([pay_mode],''), ' ', ''), '-', '')) LIKE '%MPESA%'
                              AND UPPER(REPLACE(REPLACE(ISNULL([pay_mode],''), ' ', ''), '-', '')) NOT LIKE '%PAYBILL%')
                        THEN CAST([paid_amount] AS DECIMAL(18,2)) ELSE 0 
                    END), 0) as TotalMPesaCollected,
                    ISNULL(SUM(CASE 
                        WHEN UPPER(REPLACE(REPLACE(ISNULL([pay_mode],''), ' ', ''), '-', '')) IN ('FREEOFCHARGE','FOC') 
                        THEN 1 ELSE 0 
                    END), 0) as TotalFocCount,
                    ISNULL(SUM(CAST([paid_amount] AS DECIMAL(18,2))), 0) as GrandTotalAmount,
                    COUNT(*) as GrandTotal
                FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited] WITH (NOLOCK)
                WHERE CAST([exit_time] AS DATE) >= @startDate 
                  AND CAST([exit_time] AS DATE) <= @endDate
                  AND [exit_time] IS NOT NULL";

                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.CommandTimeout = 120;
                    cmd.Parameters.AddWithValue("@startDate", request.StartDate.Date);
                    cmd.Parameters.AddWithValue("@endDate", request.EndDate.Date);

                    con.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            stats = new
                            {
                                totalVehicles = reader.GetInt32(0),
                                totalCharges = reader.GetDecimal(1),
                                totalCashCollected = reader.GetDecimal(2),
                                totalMPesaCollected = reader.GetDecimal(3),
                                totalFocCount = reader.GetInt32(4),
                                grandTotalAmount = reader.GetDecimal(5),
                                grandTotal = reader.GetInt32(6),
                                creditTakenCount = 0,
                                equityCount = 0,
                                advancePaidCount = 0,
                                reparkCount = 0
                            };
                        }
                    }
                }
            }

            _logger.LogInformation("Dashboard metrics retrieved successfully");
            return Json(new { success = true, data = stats });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetDashboardMetrics");

            string errorMsg = "Failed to load metrics. ";
            if (ex.Message.Contains("Timeout"))
                errorMsg += "The database is taking too long. Please try a shorter date range.";
            else
                errorMsg += ex.Message;

            return Json(new { success = false, message = errorMsg });
        }
    }

    [HttpPost]
    public IActionResult GetDayWiseExitData([FromBody] DateRangeRequest request)
    {
        try
        {
            _logger.LogInformation($"GetDayWiseExitData called: {request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd}");

            if (request.StartDate == default || request.EndDate == default)
            {
                _logger.LogWarning("Invalid date range received in GetDayWiseExitData");
                return Json(new { success = false, message = "Invalid date range" });
            }

            var data = new List<DayWiseExitData>();

            using (SqlConnection con = new SqlConnection(GetDynamicConnectionString()))
            {
                string sql = @"
                SELECT 
                    CAST(exit_time AS DATE) AS ExitDate,
                    COUNT(*) AS ExitCount
                FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited] WITH (NOLOCK)
                WHERE CAST(exit_time AS DATE) >= @StartDate 
                  AND CAST(exit_time AS DATE) <= @EndDate
                  AND exit_time IS NOT NULL
                GROUP BY CAST(exit_time AS DATE)
                ORDER BY ExitDate ASC";

                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.CommandTimeout = 120;
                    cmd.Parameters.AddWithValue("@StartDate", request.StartDate.Date);
                    cmd.Parameters.AddWithValue("@EndDate", request.EndDate.Date);

                    con.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            data.Add(new DayWiseExitData
                            {
                                Date = ((DateTime)reader["ExitDate"]).ToString("dd-MMM-yyyy"),
                                ExitCount = (int)reader["ExitCount"]
                            });
                        }
                    }
                }
            }

            _logger.LogInformation($"Day-wise exit data retrieved: {data.Count} records");
            return Json(new { success = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetDayWiseExitData");
            return Json(new { success = false, message = $"Error: {ex.Message}" });
        }
    }

    [HttpPost]
    public IActionResult GetPaymentMethodDistribution([FromBody] DateRangeRequest request)
    {
        try
        {
            _logger.LogInformation($"GetPaymentMethodDistribution called: {request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd}");

            if (request.StartDate == default || request.EndDate == default)
            {
                _logger.LogWarning("Invalid date range received in GetPaymentMethodDistribution");
                return Json(new { success = false, message = "Invalid date range" });
            }

            var paymentData = new List<PaymentDistribution>();

            using (SqlConnection con = new SqlConnection(GetDynamicConnectionString()))
            {
                string sql = @"
                SELECT
                    SUM(CASE 
                        WHEN UPPER(REPLACE(REPLACE(ISNULL([pay_mode],''), ' ', ''), '-', '')) = 'CASH' 
                        THEN CAST([paid_amount] AS DECIMAL(18,2)) ELSE 0 
                    END) as CashAmount,
                    SUM(CASE 
                        WHEN (UPPER(REPLACE(REPLACE(ISNULL([pay_mode],''), ' ', ''), '-', '')) LIKE '%MPESA%'
                              AND UPPER(REPLACE(REPLACE(ISNULL([pay_mode],''), ' ', ''), '-', '')) NOT LIKE '%PAYBILL%')
                        THEN CAST([paid_amount] AS DECIMAL(18,2)) ELSE 0 
                    END) as MPesaAmount,
                    SUM(CASE 
                        WHEN UPPER(REPLACE(REPLACE(ISNULL([pay_mode],''), ' ', ''), '-', '')) LIKE '%PAYBILL%'
                        THEN CAST([paid_amount] AS DECIMAL(18,2)) ELSE 0 
                    END) as MPesaPaybillAmount,
                    SUM(CASE 
                        WHEN UPPER(REPLACE(REPLACE(ISNULL([pay_mode],''), ' ', ''), '-', '')) IN ('FREEOFCHARGE','FOC')
                        THEN CAST([charges] AS DECIMAL(18,2)) ELSE 0 
                    END) as FocAmount
                FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited] WITH (NOLOCK)
                WHERE CAST([exit_time] AS DATE) >= @StartDate 
                  AND CAST([exit_time] AS DATE) <= @EndDate
                  AND [exit_time] IS NOT NULL";

                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.CommandTimeout = 120;
                    cmd.Parameters.AddWithValue("@StartDate", request.StartDate.Date);
                    cmd.Parameters.AddWithValue("@EndDate", request.EndDate.Date);

                    con.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            var cashAmount = reader.IsDBNull(0) ? 0m : reader.GetDecimal(0);
                            var mpesaAmount = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
                            var mpesaPaybillAmount = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
                            var focAmount = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3);

                            paymentData = new List<PaymentDistribution>
                            {
                                new PaymentDistribution { Label = "Cash",           Value = cashAmount },
                                new PaymentDistribution { Label = "M-Pesa",         Value = mpesaAmount },
                                new PaymentDistribution { Label = "M-Pesa Paybill", Value = mpesaPaybillAmount },
                                new PaymentDistribution { Label = "Free of Charge", Value = focAmount }
                            };

                            var total = paymentData.Sum(d => d.Value);
                            if (total > 0)
                            {
                                foreach (var item in paymentData)
                                    item.Percentage = (decimal)(((decimal)item.Value / total) * 100);
                            }
                        }
                    }
                }
            }

            _logger.LogInformation("Payment distribution calculated successfully");
            return Json(new { success = true, data = paymentData });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetPaymentMethodDistribution");

            string errorMsg = "Failed to load payment data. ";
            if (ex.Message.Contains("Timeout"))
                errorMsg += "The database is taking too long. Please try a shorter date range.";
            else
                errorMsg += ex.Message;

            return Json(new { success = false, message = errorMsg });
        }
    }

    // ─── Logout ───────────────────────────────────────────────────────────────
    public IActionResult Logout()
    {
        var user = HttpContext.Session.GetString(AuthSessionKey);
        _logger.LogInformation($"User '{user}' logged out");

        // Clear all role-related session keys on logout
        HttpContext.Session.Remove(AuthSessionKey);
        HttpContext.Session.Remove("OperatorType");
        HttpContext.Session.Remove("Username");

        return RedirectToAction("Login", "Utility");
    }
}

/// <summary>Request object for date range filtering</summary>
public class DateRangeRequest
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}

/// <summary>Day-wise exit data model</summary>
public class DayWiseExitData
{
    public string Date { get; set; }
    public int ExitCount { get; set; }
}

/// <summary>Payment distribution data model</summary>
public class PaymentDistribution
{
    public string Label { get; set; }
    public decimal Value { get; set; }
    public decimal Percentage { get; set; }
}
