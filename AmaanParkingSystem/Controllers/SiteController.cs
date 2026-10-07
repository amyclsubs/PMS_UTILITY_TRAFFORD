using AmaanParkingSystem.Controllers;
using AmaanParkingSystem.Models.Site;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

public class SiteController : BaseController
{
    public SiteController()
    {
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? id = null)
    {
        using var conn = new SqlConnection(GetDynamicConnectionString());

        var listSql = @"
            SELECT TOP (1000000)
                [id],[customer_id],[site_name],[client_name],[client_email],[client_site_address],
                [client_status],[created_by],[created_on],[updated_by],[updated_on],[site_id],
                ISNULL([site_type],'') AS site_type,
                ISNULL([receive_daily_exited],0) AS receive_daily_exited,
                ISNULL([receive_daily_transactions],0) AS receive_daily_transactions,
                ISNULL([receive_monthly_exited],0) AS receive_monthly_exited,
                ISNULL([receive_monthly_transactions],0) AS receive_monthly_transactions
            FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
            ORDER BY [created_on] DESC;";

        var sites = (await conn.QueryAsync<SiteModel>(listSql)).ToList();

        SiteModel editModel = null;
        if (id.HasValue)
        {
            var editSql = @"
                SELECT [id],[customer_id],[site_name],[client_name],[client_email],[client_site_address],
                       [client_status],[created_by],[created_on],[updated_by],[updated_on],[site_id],
                       ISNULL([site_type],'') AS site_type,
                       ISNULL([receive_daily_exited],0) AS receive_daily_exited,
                       ISNULL([receive_daily_transactions],0) AS receive_daily_transactions,
                       ISNULL([receive_monthly_exited],0) AS receive_monthly_exited,
                       ISNULL([receive_monthly_transactions],0) AS receive_monthly_transactions
                FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                WHERE [id]=@id;";

            editModel = await conn.QueryFirstOrDefaultAsync<SiteModel>(editSql, new { id });
        }

        ViewBag.Message = TempData["Message"];
        ViewBag.EditModel = editModel;

        return View(sites);
    }

    [HttpPost]
    public async Task<IActionResult> Save(SiteModel model)
    {
        using var conn = new SqlConnection(GetDynamicConnectionString());

        // ✅ DUPLICATE CHECK: Site Name
        var duplicateSiteCheckSql = model.id > 0
            ? @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
            WHERE LTRIM(RTRIM(LOWER(site_name))) = LTRIM(RTRIM(LOWER(@site_name))) AND id != @id"
            : @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
            WHERE LTRIM(RTRIM(LOWER(site_name))) = LTRIM(RTRIM(LOWER(@site_name)))";

        var siteExists = await conn.ExecuteScalarAsync<int>(
            duplicateSiteCheckSql, new { model.site_name, model.id }) > 0;

        if (siteExists)
        {
            TempData["Message"] = $"❌ Site Name '{model.site_name}' already exists! Please use a different site name.";
            if (model.id > 0)
                return RedirectToAction("Index", new { id = model.id });
            else
                return RedirectToAction("Index");
        }

        // normalize checkboxes (null => false handled by model binder, but keep explicit)
        model.receive_daily_exited = model.receive_daily_exited;
        model.receive_daily_transactions = model.receive_daily_transactions;
        model.receive_monthly_exited = model.receive_monthly_exited;
        model.receive_monthly_transactions = model.receive_monthly_transactions;

        if (model.id > 0)
        {
            var updateSql = @"
            UPDATE [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
            SET [customer_id]                 = @customer_id,
                [site_name]                   = @site_name,
                [client_name]                 = @client_name,
                [client_email]                = @client_email,
                [client_site_address]         = @client_site_address,
                [client_status]               = @client_status,
                [site_id]                     = @site_id,
                [site_type]                   = @site_type,
                [receive_daily_exited]        = @receive_daily_exited,
                [receive_daily_transactions]  = @receive_daily_transactions,
                [receive_monthly_exited]      = @receive_monthly_exited,
                [receive_monthly_transactions]= @receive_monthly_transactions,
                [updated_on]                  = SYSDATETIME()
            WHERE [id]=@id;";

            await conn.ExecuteAsync(updateSql, model);
            TempData["Message"] = "Site updated successfully!";
        }
        else
        {
            var insertSql = @"
            INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
            ([customer_id],[site_name],[client_name],[client_email],[client_site_address],
             [client_status],[created_by],[created_on],[site_id],
             [site_type],
             [receive_daily_exited],
             [receive_daily_transactions],
             [receive_monthly_exited],
             [receive_monthly_transactions])
            VALUES
            (@customer_id, @site_name, @client_name, @client_email, @client_site_address,
             @client_status, @created_by, SYSDATETIME(), @site_id,
             @site_type,
             @receive_daily_exited,
             @receive_daily_transactions,
             @receive_monthly_exited,
             @receive_monthly_transactions);";

            await conn.ExecuteAsync(insertSql, model);
            TempData["Message"] = "Site added successfully!";
        }

        return RedirectToAction("Index");
    }
}
