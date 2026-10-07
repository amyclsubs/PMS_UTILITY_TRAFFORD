using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AmaanParkingSystem.Models;
using AmaanParkingSystem.Filters;

namespace AmaanParkingSystem.Controllers
{
    public class OperatorController : BaseController
    {
        private readonly ILogger<OperatorController> _logger;

        public OperatorController(ILogger<OperatorController> logger)
        {
            _logger = logger;
        }

        // ─── VIEW: ADMIN only ────────────────────────────────────────────────
        // MANAGER and OPERATOR are redirected away — they cannot see this page at all.
        [RoleAuthorize("ADMIN")]
        public IActionResult OperatorRegistration(int? editId = null)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());
            var operators = conn.Query<OperatorModel>(@"
                SELECT TOP 1000 id, operator_type, operator_login_id, operator_name, operator_mobile,
                       CAST(operator_login_status AS varchar(10)) as operator_login_status,
                       CAST(operator_status AS varchar(10)) as operator_status,
                       created_by, created_on
                FROM [AMAAN_PMS].[dbo].[db_tbl_01_operator]
                ORDER BY id DESC").ToList();

            ViewBag.Operators = operators;

            if (editId.HasValue)
            {
                var editModel = conn.QuerySingleOrDefault<OperatorModel>(@"
                    SELECT id, operator_type, operator_login_id, operator_name, operator_mobile,
                           CAST(operator_login_status AS varchar(10)) as operator_login_status,
                           CAST(operator_status AS varchar(10)) as operator_status
                    FROM [AMAAN_PMS].[dbo].[db_tbl_01_operator] 
                    WHERE id = @id", new { id = editId.Value });

                if (editModel != null)
                    return View(editModel);
            }

            return View(new OperatorModel());
        }

        // ─── SAVE: ADMIN only ────────────────────────────────────────────────
        [HttpPost]
        [RoleAuthorize("ADMIN")]
        public IActionResult SaveOperator(OperatorModel model)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            if (model.id == 0) // ADD NEW
            {
                model.created_by = GetLoggedInUser();
                model.created_on = DateTime.Now;

                conn.Execute(@"
                    INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_01_operator]
                    (operator_type, operator_login_id, operator_name, operator_mobile,
                     operator_password, operator_login_status, operator_status, created_by, created_on)
                    VALUES (@operator_type, @operator_login_id, @operator_name, @operator_mobile,
                            @operator_password, @operator_login_status, @operator_status,
                            @created_by, @created_on)", model);

                TempData["SuccessMessage"] = "Operator added successfully!";
            }
            else // EDIT
            {
                model.updated_by = GetLoggedInUser();
                model.updated_on = DateTime.Now;

                var sql = @"
                    UPDATE [AMAAN_PMS].[dbo].[db_tbl_01_operator]
                    SET operator_type = @operator_type,
                        operator_login_id = @operator_login_id,
                        operator_name = @operator_name,
                        operator_mobile = @operator_mobile,
                        operator_login_status = @operator_login_status,
                        operator_status = @operator_status,
                        updated_by = @updated_by,
                        updated_on = @updated_on";

                if (!string.IsNullOrEmpty(model.operator_password))
                {
                    sql += ", operator_password = @operator_password";
                }

                sql += " WHERE id = @id";

                conn.Execute(sql, model);
                TempData["SuccessMessage"] = "Operator updated successfully!";
            }

            return RedirectToAction("OperatorRegistration");
        }
    }
}