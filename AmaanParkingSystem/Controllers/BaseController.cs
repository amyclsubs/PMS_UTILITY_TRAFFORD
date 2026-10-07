using AmaanParkingSystem.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AmaanParkingSystem.Controllers
{
    public class BaseController : Controller
    {
        // ─── Connection String Helper ──────────────────────────────────────────
        protected string GetDynamicConnectionString()
        {
            var service = HttpContext.RequestServices.GetService<IConnectionStringService>();
            var cs = service?.GetConnectionString();
            if (string.IsNullOrEmpty(cs))
                throw new InvalidOperationException(
                    "Database connection string is not configured. " +
                    "Please complete the Database Login step first.");
            return cs;
        }

        // ─── Audit / User helpers ──────────────────────────────────────────────
        protected string GetLoggedInUser()
        {
            var username = HttpContext.Session.GetString("Username")
                        ?? HttpContext.Session.GetString("UserID")
                        ?? HttpContext.Session.GetString("UserId")
                        ?? HttpContext.Session.GetString("user_id")
                        ?? HttpContext.Session.GetString("LoggedInUser")
                        ?? HttpContext.Session.GetString("operator_id")
                        ?? HttpContext.Session.GetString("OperatorID");

            if (string.IsNullOrEmpty(username) && User?.Identity?.IsAuthenticated == true)
                username = User.Identity.Name;

            return username ?? "System";
        }

        protected Dictionary<string, string> GetAllSessionKeys()
        {
            var sessionData = new Dictionary<string, string>();
            var keysToCheck = new[]
            {
                "Username", "UserID", "UserId", "user_id",
                "LoggedInUser", "operator_id", "OperatorID",
                "UserName", "User", "LoginUser"
            };

            foreach (var key in keysToCheck)
            {
                var value = HttpContext.Session.GetString(key);
                if (!string.IsNullOrEmpty(value))
                    sessionData[key] = value;
            }

            return sessionData;
        }

        // ─── RBAC: Role Helper Methods ─────────────────────────────────────────

        /// <summary>
        /// Returns the OperatorType stored in session (OPERATOR / MANAGER / ADMIN).
        /// Returns empty string if not set.
        /// </summary>
        protected string GetOperatorType()
        {
            return HttpContext.Session.GetString("OperatorType") ?? string.Empty;
        }

        /// <summary>
        /// Returns true if the logged-in user is ADMIN.
        /// </summary>
        protected bool IsAdmin()
        {
            return string.Equals(GetOperatorType(), "ADMIN", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true if the logged-in user is MANAGER.
        /// </summary>
        protected bool IsManager()
        {
            return string.Equals(GetOperatorType(), "MANAGER", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true if the logged-in user is OPERATOR (read-only).
        /// </summary>
        protected bool IsOperator()
        {
            return string.Equals(GetOperatorType(), "OPERATOR", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true if the user can perform write operations (Add / Edit / Delete).
        /// MANAGER and ADMIN can write. OPERATOR cannot.
        /// </summary>
        protected bool CanWrite()
        {
            return IsAdmin() || IsManager();
        }

        /// <summary>
        /// Returns a 403 JSON result for AJAX calls blocked by role.
        /// </summary>
        protected IActionResult ForbiddenJson()
        {
            Response.StatusCode = 403;
            return Json(new { success = false, message = "Access denied. You do not have permission to perform this action." });
        }

        /// <summary>
        /// Redirects back with a denial TempData message for non-AJAX calls blocked by role.
        /// </summary>
        protected IActionResult ForbiddenRedirect(string redirectAction = "Index", string redirectController = null)
        {
            TempData["ErrorMessage"] = "Access denied. You do not have permission to perform this action.";
            if (redirectController != null)
                return RedirectToAction(redirectAction, redirectController);
            return RedirectToAction(redirectAction);
        }

        // ─── OnActionExecuting ────────────────────────────────────────────────
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var sessionUser = context.HttpContext.Session.GetString("UtilityUser");
            ViewBag.CurrentUser = string.IsNullOrEmpty(sessionUser) ? "System" : sessionUser;
            ViewBag.CurrentDateTime = DateTime.Now;

            // Expose role to all views via ViewBag so Razor can show/hide buttons
            var operatorType = context.HttpContext.Session.GetString("OperatorType") ?? string.Empty;
            ViewBag.OperatorType = operatorType;
            ViewBag.IsAdmin = string.Equals(operatorType, "ADMIN", StringComparison.OrdinalIgnoreCase);
            ViewBag.IsManager = string.Equals(operatorType, "MANAGER", StringComparison.OrdinalIgnoreCase);
            ViewBag.IsOperator = string.Equals(operatorType, "OPERATOR", StringComparison.OrdinalIgnoreCase);
            ViewBag.CanWrite = !string.Equals(operatorType, "OPERATOR", StringComparison.OrdinalIgnoreCase);

            base.OnActionExecuting(context);
        }

        protected (string username, DateTime timestamp) GetAuditInfo()
            => (GetLoggedInUser(), DateTime.Now);
    }
}
