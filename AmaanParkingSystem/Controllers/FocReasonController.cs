using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;
using System.ComponentModel.DataAnnotations;
using AmaanParkingSystem.Filters;

namespace AmaanParkingSystem.Controllers
{
    public class FocReasonController : BaseController
    {
        public FocReasonController()
        {
        }

        public class FocReasonVM
        {
            public string? site_name { get; set; }

            [Required]
            public string Reason { get; set; }

            public string? OriginalSiteName { get; set; }
            public string? OriginalReason { get; set; }

            public List<FocReasonVM> FocReasonList { get; set; } = new List<FocReasonVM>();
        }

        // ─── VIEW: All roles can view FOC Reasons ────────────────────────────
        [HttpGet]
        public IActionResult Index(string? editSiteName = null, string? editReason = null)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            var model = new FocReasonVM();

            model.FocReasonList = conn.Query<FocReasonVM>(@"
                SELECT site_name, Reason
                FROM [AMAAN_PMS].[dbo].[db_tbl_25_FOC_Reason]
                ORDER BY
                    CASE WHEN site_name IS NULL THEN 0 ELSE 1 END,
                    site_name,
                    Reason
            ").ToList();

            if (!string.IsNullOrWhiteSpace(editReason))
            {
                model.site_name = editSiteName;
                model.Reason = editReason;
                model.OriginalSiteName = editSiteName;
                model.OriginalReason = editReason;
                ViewBag.IsEdit = true;
            }

            return View("~/Views/Site/FocReasons.cshtml", model);
        }

        // ─── SAVE: Restricted to ADMIN and MANAGER only ──────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RoleAuthorize("ADMIN", "MANAGER")]
        public IActionResult Save(FocReasonVM model)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            model.site_name = string.IsNullOrWhiteSpace(model.site_name) ? null : model.site_name.Trim();
            model.Reason = string.IsNullOrWhiteSpace(model.Reason) ? null : model.Reason.Trim();
            model.OriginalSiteName = string.IsNullOrWhiteSpace(model.OriginalSiteName) ? null : model.OriginalSiteName.Trim();
            model.OriginalReason = string.IsNullOrWhiteSpace(model.OriginalReason) ? null : model.OriginalReason.Trim();

            if (string.IsNullOrWhiteSpace(model.Reason))
            {
                TempData["ErrorMessage"] = "Reason is required.";
                return RedirectToAction("Index");
            }

            if (string.IsNullOrWhiteSpace(model.OriginalReason))
            {
                var exists = conn.ExecuteScalar<int>(@"
                    SELECT COUNT(*)
                    FROM [AMAAN_PMS].[dbo].[db_tbl_25_FOC_Reason]
                    WHERE
                        (
                            (site_name = @site_name)
                            OR (site_name IS NULL AND @site_name IS NULL)
                        )
                        AND LTRIM(RTRIM(LOWER(Reason))) = LTRIM(RTRIM(LOWER(@Reason)))
                ", new
                {
                    model.site_name,
                    model.Reason
                });

                if (exists > 0)
                {
                    TempData["ErrorMessage"] = "FOC Reason already exists.";
                    return RedirectToAction("Index");
                }

                conn.Execute(@"
                    INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_25_FOC_Reason]
                    (site_name, Reason)
                    VALUES
                    (@site_name, @Reason)
                ", new
                {
                    model.site_name,
                    model.Reason
                });

                TempData["SuccessMessage"] = "FOC Reason added successfully.";
            }
            else
            {
                var duplicateExists = conn.ExecuteScalar<int>(@"
                    SELECT COUNT(*)
                    FROM [AMAAN_PMS].[dbo].[db_tbl_25_FOC_Reason]
                    WHERE
                        (
                            (site_name = @site_name)
                            OR (site_name IS NULL AND @site_name IS NULL)
                        )
                        AND LTRIM(RTRIM(LOWER(Reason))) = LTRIM(RTRIM(LOWER(@Reason)))
                        AND NOT
                        (
                            (
                                (site_name = @OriginalSiteName)
                                OR (site_name IS NULL AND @OriginalSiteName IS NULL)
                            )
                            AND LTRIM(RTRIM(LOWER(Reason))) = LTRIM(RTRIM(LOWER(@OriginalReason)))
                        )
                ", new
                {
                    model.site_name,
                    model.Reason,
                    model.OriginalSiteName,
                    model.OriginalReason
                });

                if (duplicateExists > 0)
                {
                    TempData["ErrorMessage"] = "FOC Reason already exists.";
                    return RedirectToAction("Index");
                }

                var rows = conn.Execute(@"
                    UPDATE [AMAAN_PMS].[dbo].[db_tbl_25_FOC_Reason]
                    SET site_name = @site_name,
                        Reason = @Reason
                    WHERE
                        (
                            (site_name = @OriginalSiteName)
                            OR (site_name IS NULL AND @OriginalSiteName IS NULL)
                        )
                        AND LTRIM(RTRIM(LOWER(Reason))) = LTRIM(RTRIM(LOWER(@OriginalReason)))
                ", new
                {
                    model.site_name,
                    model.Reason,
                    model.OriginalSiteName,
                    model.OriginalReason
                });

                if (rows == 0)
                {
                    TempData["ErrorMessage"] = "Record not found for update.";
                    return RedirectToAction("Index");
                }

                TempData["SuccessMessage"] = "FOC Reason updated successfully.";
            }

            return RedirectToAction("Index");
        }

        // ─── DELETE: Restricted to ADMIN and MANAGER only ────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RoleAuthorize("ADMIN", "MANAGER")]
        public IActionResult Delete(string? site_name, string reason)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            site_name = string.IsNullOrWhiteSpace(site_name) ? null : site_name.Trim();
            reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

            if (string.IsNullOrWhiteSpace(reason))
            {
                TempData["ErrorMessage"] = "Invalid delete request.";
                return RedirectToAction("Index");
            }

            var rows = conn.Execute(@"
                DELETE FROM [AMAAN_PMS].[dbo].[db_tbl_25_FOC_Reason]
                WHERE
                    (
                        (site_name = @site_name)
                        OR (site_name IS NULL AND @site_name IS NULL)
                    )
                    AND LTRIM(RTRIM(LOWER(Reason))) = LTRIM(RTRIM(LOWER(@reason)))
            ", new
            {
                site_name,
                reason
            });

            if (rows == 0)
            {
                TempData["ErrorMessage"] = "Record not found for delete.";
                return RedirectToAction("Index");
            }

            TempData["SuccessMessage"] = "FOC Reason deleted successfully.";
            return RedirectToAction("Index");
        }
    }
}
