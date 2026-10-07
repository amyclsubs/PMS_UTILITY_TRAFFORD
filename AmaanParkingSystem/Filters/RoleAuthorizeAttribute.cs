// File: Filters/RoleAuthorizeAttribute.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AmaanParkingSystem.Filters
{
    /// <summary>
    /// Restricts access to the decorated controller or action to users
    /// whose OperatorType matches one of the allowed roles.
    ///
    /// Usage examples:
    ///   [RoleAuthorize("ADMIN")]                   — ADMIN only
    ///   [RoleAuthorize("ADMIN", "MANAGER")]        — ADMIN or MANAGER
    ///   [RoleAuthorize("ADMIN", "MANAGER", "OPERATOR")] — all roles (same as no restriction)
    ///
    /// The attribute redirects non-AJAX requests to Utility/Index with an
    /// ErrorMessage, and returns HTTP 403 JSON for AJAX/API requests.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public class RoleAuthorizeAttribute : ActionFilterAttribute
    {
        private readonly string[] _allowedRoles;

        public RoleAuthorizeAttribute(params string[] allowedRoles)
        {
            _allowedRoles = allowedRoles
                .Select(r => r.Trim().ToUpperInvariant())
                .ToArray();
        }

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var operatorType = context.HttpContext.Session
                .GetString("OperatorType") ?? string.Empty;

            bool isAllowed = _allowedRoles.Contains(
                operatorType.Trim().ToUpperInvariant());

            if (!isAllowed)
            {
                // Determine if this is an AJAX / API call
                bool isAjax = context.HttpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest"
                           || context.HttpContext.Request.ContentType?.Contains("application/json") == true;

                if (isAjax)
                {
                    context.HttpContext.Response.StatusCode = 403;
                    context.Result = new JsonResult(new
                    {
                        success = false,
                        message = "Access denied. You do not have permission to perform this action."
                    });
                }
                else
                {
                    context.HttpContext.Session.SetString(
                        "ErrorMessage",
                        "Access denied. You do not have permission to access that page.");

                    // Redirect to Utility Index (they are already logged in — just wrong role)
                    context.Result = new RedirectToActionResult("Index", "Utility", null);
                }
            }

            base.OnActionExecuting(context);
        }
    }
}
