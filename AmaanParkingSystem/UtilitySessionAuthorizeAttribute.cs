// File: UtilitySessionAuthorizeAttribute.cs
using AmaanParkingSystem.Controllers;
using AmaanParkingSystem.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

/// <summary>
/// Global filter applied in Program.cs.
/// Responsibilities:
///   1. Allow DatabaseLogin controller through without any auth.
///   2. Ensure a DB connection string is configured.
///   3. Ensure the user has logged in (UtilityUser session key is set).
///   4. Block MANAGER role from accessing the Utilities section controllers
///      (Backup, EmailConfig, B2Notification).
/// </summary>
public class UtilitySessionAuthorizeAttribute : ActionFilterAttribute
{
    // Controllers that belong to the Utilities section in the sidebar.
    // MANAGER is blocked from all of these.
    private static readonly HashSet<string> UtilitiesControllers = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "Backup",
        "EmailConfig",
        "B2Notification"
    };

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var route = context.ActionDescriptor.RouteValues;
        var controller = route["controller"];
        var action = route["action"];

        // ── 1. Always allow DatabaseLogin through. ──────────────────────────
        if (controller?.Equals("DatabaseLogin", StringComparison.OrdinalIgnoreCase) == true)
        {
            base.OnActionExecuting(context);
            return;
        }

        // ── 2. Check that the DB connection has been established. ───────────
        var dbEstablished = context.HttpContext.Session.GetString(
            DatabaseLoginController.DbSessionKey);

        if (string.IsNullOrEmpty(dbEstablished))
        {
            var connStrService = context.HttpContext.RequestServices
                .GetService<IConnectionStringService>();

            if (connStrService == null || !connStrService.IsConfigured)
            {
                context.Result = new RedirectToActionResult("Index", "DatabaseLogin", null);
                return;
            }

            // Service is configured but session expired — restore flag, then
            // redirect to application login.
            context.HttpContext.Session.SetString(
                DatabaseLoginController.DbSessionKey, "true");

            if (!(controller?.Equals("Utility", StringComparison.OrdinalIgnoreCase) == true
                  && action?.Equals("Login", StringComparison.OrdinalIgnoreCase) == true))
            {
                context.Result = new RedirectToActionResult("Login", "Utility", null);
                return;
            }
        }

        // ── 3. Allow Utility/Login page through without UtilityUser check. ──
        if (controller?.Equals("Utility", StringComparison.OrdinalIgnoreCase) == true
            && action?.Equals("Login", StringComparison.OrdinalIgnoreCase) == true)
        {
            base.OnActionExecuting(context);
            return;
        }

        // ── 4. All other pages require a logged-in UtilityUser. ─────────────
        var user = context.HttpContext.Session.GetString("UtilityUser");
        if (string.IsNullOrEmpty(user))
        {
            context.Result = new RedirectToActionResult("Login", "Utility", null);
            return;
        }

        // ── 5. RBAC: Block MANAGER from Utilities section. ──────────────────
        //       OPERATOR is NOT blocked here at route level because they can
        //       still VIEW utilities pages — they are only blocked from
        //       write actions by [RoleAuthorize] on individual action methods.
        //       MANAGER however must not even see Utilities pages.
        var operatorType = context.HttpContext.Session.GetString("OperatorType") ?? string.Empty;
        bool isManagerOrOperator = string.Equals(operatorType, "MANAGER", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(operatorType, "OPERATOR", StringComparison.OrdinalIgnoreCase);

        if (isManagerOrOperator && controller != null && UtilitiesControllers.Contains(controller))
        {
            bool isAjax = context.HttpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest"
                       || context.HttpContext.Request.ContentType?.Contains("application/json") == true;

            if (isAjax)
            {
                context.HttpContext.Response.StatusCode = 403;
                context.Result = new JsonResult(new
                {
                    success = false,
                    message = "Access denied. Managers cannot access the Utilities section."
                });
            }
            else
            {
                // Store a message in TempData so the toast fires on Index page
                context.HttpContext.Session.SetString(
                    "RbacDeniedMessage",
                    "Access denied. The Utilities section is restricted to Administrators only.");

                context.Result = new RedirectToActionResult("Index", "Utility", null);
            }
            return;
        }

        base.OnActionExecuting(context);
    }
}