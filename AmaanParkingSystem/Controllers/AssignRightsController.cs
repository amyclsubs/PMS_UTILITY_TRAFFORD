using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using AmaanParkingSystem.Models;
using System.Text.Json;

namespace AmaanParkingSystem.Controllers
{
    public class AssignRightsController : BaseController
    {
        public AssignRightsController()
        {
        }

        // LIST ALL OPERATORS
        public IActionResult Index()
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());
            var operators = conn.Query<AssignRightsListModel>(@"
                SELECT id, operator_type, operator_login_id, operator_name, 
                       operator_mobile, operator_permissions,
                       CAST(operator_status AS varchar(10)) as operator_status
                FROM [AMAAN_PMS].[dbo].[db_tbl_01_operator]
                WHERE operator_status = '1'
                ORDER BY operator_name").ToList();

            // Calculate permission statistics
            foreach (var op in operators)
            {
                op.has_permissions = !string.IsNullOrEmpty(op.operator_permissions);
                if (op.has_permissions)
                {
                    try
                    {
                        var perms = JsonSerializer.Deserialize<Dictionary<string, ModulePermission>>(op.operator_permissions);
                        op.total_modules = perms.Count;
                        op.allowed_modules = perms.Count(p => p.Value.can_view);
                    }
                    catch { }
                }
            }

            return View(operators);
        }

        // EDIT PERMISSIONS FOR SPECIFIC OPERATOR
        [HttpGet]
        public IActionResult Edit(int operatorId)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            var op = conn.QuerySingleOrDefault<dynamic>(@"
                SELECT id, operator_type, operator_login_id, operator_name, 
                       operator_mobile, operator_permissions
                FROM [AMAAN_PMS].[dbo].[db_tbl_01_operator]
                WHERE id = @id", new { id = operatorId });

            if (op == null)
            {
                TempData["ErrorMessage"] = "Operator not found!";
                return RedirectToAction("Index");
            }

            var model = new OperatorPermissionModel
            {
                operator_id = (int)op.id,
                operator_name = op.operator_name?.ToString() ?? "",
                operator_type = op.operator_type?.ToString() ?? "",
                operator_mobile = op.operator_mobile?.ToString() ?? "",
                modules = GetAllModules()
            };

            // Load saved permissions
            string permissionsJson = op.operator_permissions?.ToString();
            if (!string.IsNullOrEmpty(permissionsJson))
            {
                try
                {
                    var savedPermissions = JsonSerializer.Deserialize<Dictionary<string, ModulePermission>>(permissionsJson);
                    foreach (var kvp in savedPermissions ?? new())
                    {
                        if (model.modules.ContainsKey(kvp.Key))
                            model.modules[kvp.Key] = kvp.Value;
                    }
                }
                catch { }
            }

            return View(model);
        }

        /// NEW: Save permissions via JSON (AJAX)
        [HttpPost]
        public IActionResult SavePermissionsJson([FromBody] PermissionSaveRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.permissions_json))
                {
                    return Json(new { success = false, message = "No permissions data received!" });
                }

                using var conn = new SqlConnection(GetDynamicConnectionString());

                conn.Execute(@"
            UPDATE [AMAAN_PMS].[dbo].[db_tbl_01_operator]
            SET operator_permissions = @permissionsJson,
                updated_by = @updatedBy,
                updated_on = GETDATE()
            WHERE id = @operator_id",
                    new
                    {
                        operator_id = request.operator_id,
                        permissionsJson = request.permissions_json,
                        updatedBy = GetLoggedInUser()
                    });

                return Json(new { success = true, message = "Permissions saved successfully!" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // Request model for JSON
        public class PermissionSaveRequest
        {
            public int operator_id { get; set; }
            public string permissions_json { get; set; }
        }


        // GET ALL MODULES BASED ON YOUR CONTROLLERS
        private Dictionary<string, ModulePermission> GetAllModules()
        {
            return new Dictionary<string, ModulePermission>
            {
                // Based on your Controllers folder structure
                ["ANPR"] = new ModulePermission(),
                ["Backup"] = new ModulePermission(),
                ["CardWiseReport"] = new ModulePermission(),
                ["Collection"] = new ModulePermission(),
                ["CompaniesController"] = new ModulePermission(),
                ["CompanyWiseReport"] = new ModulePermission(),
                ["EmailConfig"] = new ModulePermission(),
                ["FocReport"] = new ModulePermission(),
                ["Home"] = new ModulePermission { can_view = true }, // Default access
                ["LowBalanceAlerts"] = new ModulePermission(),
                ["OperatorManagement"] = new ModulePermission(),
                ["ParkerCompany"] = new ModulePermission(),
                ["ParkerCompanyMaster"] = new ModulePermission(),
                ["Site"] = new ModulePermission(),
                ["TopUpReport"] = new ModulePermission(),
                ["UserManagement"] = new ModulePermission(),
                ["Utility"] = new ModulePermission()
            };
        }
    }
}
