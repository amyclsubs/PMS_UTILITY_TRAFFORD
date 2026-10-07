using AmaanParkingSystem.Controllers;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

public class CardWiseReportController : BaseController
{
    public CardWiseReportController()
    {
    }

    [HttpGet]
    public IActionResult Index() => View("~/Views/Reports/CardWiseReport.cshtml");

    // Search autocomplete from allocated cards
    [HttpGet]
    public async Task<IActionResult> SearchCards(string q)
    {
        using var conn = new SqlConnection(GetDynamicConnectionString());
        string sql = @"
            SELECT TOP 20 cardno, cardname
            FROM AMAAN_PMS.dbo.db_tbl_11_cards_allocated
            WHERE cardname LIKE @Q OR cardno LIKE @Q
            ORDER BY cardname";
        var results = await conn.QueryAsync(sql, new { Q = "%" + q + "%" });
        return Json(results.Select(r => new { cardno = r.cardno, cardname = r.cardname }));
    }

    // Main report using exited car transactions
    [HttpPost]
    public async Task<IActionResult> GetReport([FromBody] CardWiseReportRequest req)
    {
        using var conn = new SqlConnection(GetDynamicConnectionString());
        string sql = @"
            SELECT TOP 1000 *
            FROM AMAAN_PMS.dbo.db_tbl_15_car_exited
            WHERE (cardno = @card OR card_name = @card)
                AND (@from IS NULL OR entry_time >= @from)
                AND (@to IS NULL OR exit_time <= @to)
                AND (@month IS NULL OR MONTH(entry_time) = @month)
                AND (@year IS NULL OR YEAR(entry_time) = @year)
            ORDER BY entry_time DESC";
        var data = await conn.QueryAsync(sql, new
        {
            card = req.Card,
            from = req.From,
            to = req.To,
            month = req.Month,
            year = req.Year
        });
        return Json(data);
    }
}
public class CardWiseReportRequest
{
    public string Card { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public int? Month { get; set; }
    public int? Year { get; set; }
}
