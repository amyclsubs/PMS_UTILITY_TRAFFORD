using AmaanParkingSystem.Controllers;
using AmaanParkingSystem.Models.UserManagement;
using AmaanParkingSystem.Services;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.IO;
using System.Text.RegularExpressions;

public class CompaniesController : BaseController
{
    private readonly EmailReportService _emailService;
    private readonly ILogger<CompaniesController> _logger;

    public CompaniesController(
        EmailReportService emailService,
        ILogger<CompaniesController> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult CompanyHierarchy()
    {
        using var conn = new SqlConnection(GetDynamicConnectionString());

        // ✅ STEP 1: Fetch SITES where site_type = 'Site'
        var sites = conn.Query<SiteHierarchyModel>(
            "SELECT id, customer_id, site_name, client_name, client_email, client_site_address, client_status FROM db_tbl_03_site_masters WHERE site_type = 'Site' ORDER BY site_name ASC"
        ).ToList();

        // Fetch card type remarks
        var cardTypes = conn.Query<CardMasterModel>(
            "SELECT card_type, Remark FROM db_tbl_10_cards_master"
        ).ToList();

        // ✅ STEP 2: For each Site, fetch its Master Companies
        foreach (var site in sites)
        {
            site.MasterCompanies = conn.Query<MasterCompanyModel>(
                "SELECT * FROM db_tbl_08A_parkers_company_master WHERE customer_id = @customerId ORDER BY car_parkers_company_name ASC",
                new { customerId = site.customer_id }
            ).ToList();

            // ✅ STEP 3: For each Master Company, fetch Sub Companies
            foreach (var master in site.MasterCompanies)
            {
                master.Site_Name = site.site_name;

                master.SubCompanies = conn.Query<SubCompanyModel>(
                    "SELECT *, card_parkers_company_password FROM db_tbl_08_parkers_company WHERE car_parkers_company_id = @cid",
                    new { cid = master.car_parkers_company_id }
                ).ToList();

                // ✅ STEP 4: For each Sub Company, fetch Cards
                foreach (var sub in master.SubCompanies)
                {
                    sub.Cards = conn.Query<AllocatedCardModel>(
                        "SELECT cardname, card_type, car_plate_no, car_owner_name, car_owner_contact_no, status, expiry_date, access_rights FROM db_tbl_11_cards_allocated WHERE car_parkers_sub_co_id = @sid",
                        new { sid = sub.car_parkers_sub_co_id }
                    ).ToList();

                    // Map card type remark
                    foreach (var card in sub.Cards)
                    {
                        var cardTypeMaster = cardTypes.FirstOrDefault(ct => ct.card_type == card.card_type);
                        card.card_type_remark = cardTypeMaster?.Remark ?? card.card_type;
                    }

                    sub.Total_Cards_Under_Sub_Co = sub.Cards.Count.ToString();
                }
            }
        }

        // STEP 5: Detect duplicates across all levels
        var allMasterPhones = new List<string>();
        var allMasterEmails = new List<string>();
        var allSubPhones = new List<string>();
        var allSubEmails = new List<string>();
        var allCardPhones = new List<string>();

        foreach (var site in sites)
        {
            foreach (var master in site.MasterCompanies)
            {
                if (!string.IsNullOrEmpty(master.car_parkers_contact_number))
                    allMasterPhones.Add(master.car_parkers_contact_number);
                if (!string.IsNullOrEmpty(master.car_parkers_email))
                    allMasterEmails.Add(master.car_parkers_email);

                foreach (var sub in master.SubCompanies)
                {
                    if (!string.IsNullOrEmpty(sub.sub_co_car_parkers_contact_number))
                        allSubPhones.Add(sub.sub_co_car_parkers_contact_number);
                    if (!string.IsNullOrEmpty(sub.sub_co_car_parkers_email))
                        allSubEmails.Add(sub.sub_co_car_parkers_email);

                    foreach (var card in sub.Cards)
                    {
                        if (!string.IsNullOrEmpty(card.car_owner_contact_no))
                            allCardPhones.Add(card.car_owner_contact_no);
                    }
                }
            }
        }

        // Find duplicates
        ViewBag.DuplicateMasterPhones = allMasterPhones
            .GroupBy(p => p)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        ViewBag.DuplicateMasterEmails = allMasterEmails
            .GroupBy(e => e)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        ViewBag.DuplicateSubPhones = allSubPhones
            .GroupBy(p => p)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        ViewBag.DuplicateSubEmails = allSubEmails
            .GroupBy(e => e)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        ViewBag.DuplicateCardPhones = allCardPhones
            .GroupBy(p => p)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        return View("~/Views/UserManagement/CompanyHierarchy.cshtml", sites);
    }

    [HttpGet]
    public IActionResult ExportMasterCompanyCsv(string companyId)
    {
        using var conn = new SqlConnection(GetDynamicConnectionString());

        var sites = conn.Query<SiteMasterModel>(
            "SELECT id, customer_id, site_name FROM db_tbl_03_site_masters"
        ).ToList();

        // Fetch card type remarks from Card Master table
        var cardTypes = conn.Query<CardMasterModel>(
           "SELECT card_type, Remark FROM db_tbl_10_cards_master").ToList();

        var master = conn.Query<MasterCompanyModel>(
            "SELECT * FROM db_tbl_08A_parkers_company_master WHERE car_parkers_company_id = @cid",
            new { cid = companyId }
        ).FirstOrDefault();

        if (master == null)
            return NotFound("Company not found");

        master.Site_Name = sites.FirstOrDefault(s => s.customer_id == master.customer_id)?.site_name ?? "N/A";

        master.SubCompanies = conn.Query<SubCompanyModel>(
            @"SELECT *, card_parkers_company_password 
      FROM db_tbl_08_parkers_company 
      WHERE car_parkers_company_id = @cid",
            new { cid = companyId }
        ).ToList();

        foreach (var sub in master.SubCompanies)
        {
            sub.Cards = conn.Query<AllocatedCardModel>(
                "SELECT cardname, card_type, car_plate_no, car_owner_name, car_owner_contact_no, status, expiry_date, access_rights FROM db_tbl_11_cards_allocated WHERE car_parkers_sub_co_id = @sid",
                new { sid = sub.car_parkers_sub_co_id }
            ).ToList();

            // Map card type remark for each card
            foreach (var card in sub.Cards)
            {
                var cardTypeMaster = cardTypes.FirstOrDefault(ct => ct.card_type == card.card_type);
                card.card_type_remark = cardTypeMaster?.Remark ?? card.card_type;
            }

            // Calculate total cards count from Cards collection
            sub.Total_Cards_Under_Sub_Co = sub.Cards.Count.ToString();
        }

        var excel = BuildColoredExcelWithNpoi(new List<MasterCompanyModel> { master });

        // Clean company name for filename (remove special characters)
        string cleanCompanyName = Regex.Replace(
            master.car_parkers_company_name ?? "Company",
            @"[^a-zA-Z0-9_\-]",
            "_"
        );

        string filename = $"{cleanCompanyName}_{companyId}_Card List_{DateTime.Now:yyyyMMdd}.xlsx";

        return File(excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
    }

    [HttpGet]
    public IActionResult ExportAllCompaniesCsv()
    {
        using var conn = new SqlConnection(GetDynamicConnectionString());

        var sites = conn.Query<SiteMasterModel>(
            "SELECT id, customer_id, site_name FROM db_tbl_03_site_masters"
        ).ToList();

        // Fetch card type remarks from Card Master table
        var cardTypes = conn.Query<CardMasterModel>(
           "SELECT card_type, Remark FROM db_tbl_10_cards_master").ToList();

        var masters = conn.Query<MasterCompanyModel>(
            "SELECT * FROM db_tbl_08A_parkers_company_master ORDER BY car_parkers_company_name ASC"
        ).ToList();

        foreach (var master in masters)
        {
            master.Site_Name = sites.FirstOrDefault(s => s.customer_id == master.customer_id)?.site_name ?? "N/A";

            master.SubCompanies = conn.Query<SubCompanyModel>(
                @"SELECT *, card_parkers_company_password 
          FROM db_tbl_08_parkers_company 
          WHERE car_parkers_company_id = @cid",
                new { cid = master.car_parkers_company_id }
            ).ToList();

            foreach (var sub in master.SubCompanies)
            {
                sub.Cards = conn.Query<AllocatedCardModel>(
                    "SELECT cardname, card_type, car_plate_no, car_owner_name, car_owner_contact_no, status, expiry_date, access_rights FROM db_tbl_11_cards_allocated WHERE car_parkers_sub_co_id = @sid",
                    new { sid = sub.car_parkers_sub_co_id }
                ).ToList();

                // Map card type remark for each card
                foreach (var card in sub.Cards)
                {
                    var cardTypeMaster = cardTypes.FirstOrDefault(ct => ct.card_type == card.card_type);
                    card.card_type_remark = cardTypeMaster?.Remark ?? card.card_type;
                }

                // Calculate total cards count from Cards collection
                sub.Total_Cards_Under_Sub_Co = sub.Cards.Count.ToString();
            }
        }

        var excel = BuildColoredExcelWithNpoi(masters);
        return File(excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "AllCompanies_CardList.xlsx");
    }

    [HttpPost]
    public async Task<IActionResult> EmailCompanyHierarchy([FromBody] CompanyEmailRequest req)
    {
        try
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            var sites = conn.Query<SiteMasterModel>(
                "SELECT id, customer_id, site_name FROM db_tbl_03_site_masters"
            ).ToList();

            // Fetch card type remarks from Card Master table
            var cardTypes = conn.Query<CardMasterModel>(
               "SELECT card_type, Remark FROM db_tbl_10_cards_master").ToList();

            var master = conn.Query<MasterCompanyModel>(
                "SELECT * FROM db_tbl_08A_parkers_company_master WHERE car_parkers_company_id = @cid",
                new { cid = req.companyId }
            ).FirstOrDefault();

            if (master == null)
                return BadRequest("Company not found.");

            master.Site_Name = sites.FirstOrDefault(s => s.customer_id == master.customer_id)?.site_name ?? "N/A";

            master.SubCompanies = conn.Query<SubCompanyModel>(
                "SELECT *, card_parkers_company_password FROM db_tbl_08_parkers_company WHERE car_parkers_company_id = @cid",
                new { cid = req.companyId }
            ).ToList();

            foreach (var sub in master.SubCompanies)
            {
                sub.Cards = conn.Query<AllocatedCardModel>(
                    "SELECT cardname, card_type, car_plate_no, car_owner_name, car_owner_contact_no, status, expiry_date, access_rights FROM db_tbl_11_cards_allocated WHERE car_parkers_sub_co_id = @sid",
                    new { sid = sub.car_parkers_sub_co_id }
                ).ToList();

                // Map card type remark for each card
                foreach (var card in sub.Cards)
                {
                    var cardTypeMaster = cardTypes.FirstOrDefault(ct => ct.card_type == card.card_type);
                    card.card_type_remark = cardTypeMaster?.Remark ?? card.card_type;
                }

                // Calculate total cards count from Cards collection
                sub.Total_Cards_Under_Sub_Co = sub.Cards.Count.ToString();
            }

            // Generate the Excel file (exactly the same as export)
            var excelBytes = BuildColoredExcelWithNpoi(new List<MasterCompanyModel> { master });

            // Prepare recipients: Master email + 3 fixed emails
            var recipients = new List<string>
            {
                master.car_parkers_email ?? string.Empty,
                "amycl.nextgenmall.parking@gmail.com",
                "amycl.office@gmail.com",
                "amycl.kenya@gmail.com"
            };

            recipients = recipients.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();

            // Use subject and body from UI, with fallback defaults only if empty
            string emailSubject = string.IsNullOrWhiteSpace(req.subject)
                ? "Company Card List Report"
                : req.subject;

            string emailBody = string.IsNullOrWhiteSpace(req.body)
                ? "Please find attached the company Card List report."
                : req.body;

            // Send email with attachment using subject/body from UI
            bool emailSent = await _emailService.SendHierarchyReportWithAttachmentAsync(
                master.id,
                recipients,
                emailSubject,
                emailBody,
                excelBytes,
                $"{master.car_parkers_company_name}_{DateTime.Now:yyyyMMdd}_Card List.xlsx"
            );

            if (emailSent)
                return Ok(new { success = true, message = $"Email sent to {recipients.Count} recipients" });
            else
                return StatusCode(500, new { success = false, message = "Failed to send email, check server logs." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending company hierarchy email.");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public IActionResult UpdateMasterCompany([FromBody] UpdateMasterCompanyRequest req)
    {
        try
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            var sql = @"UPDATE db_tbl_08A_parkers_company_master 
                    SET car_parkers_contact_person = @ContactPerson,
                        car_parkers_email = @Email, 
                        car_parkers_contact_number = @Phone,
                        updated_on = GETDATE()
                    WHERE car_parkers_company_id = @CompanyId";

            var rowsAffected = conn.Execute(sql, new
            {
                ContactPerson = req.contactPerson,
                Email = req.email,
                Phone = req.phone,
                CompanyId = req.companyId
            });

            if (rowsAffected > 0)
            {
                _logger.LogInformation("Master Company {CompanyId} updated successfully", req.companyId);
                return Ok(new { success = true, message = "Master Company updated successfully" });
            }
            else
            {
                return Ok(new { success = false, message = "No records updated. Company not found." });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating master company");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
    [HttpGet]
    public IActionResult GetCardTypeSummary()
    {
        try
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            string sql = @"
            SELECT 
                Remark,
                card_type,
                total_card,
                total_active_card,
                total_deactive_card
            FROM [AMAAN_PMS].[dbo].[db_tbl_10_cards_master]
            ORDER BY Remark ASC";

            var cardTypes = conn.Query<CardTypeSummary>(sql).ToList();

            return Json(new { success = true, data = cardTypes });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching card type summary");
            return Json(new { success = false, message = ex.Message });
        }
    }

    // Add this class at the bottom of your controller
    public class CardTypeSummary
    {
        public string Remark { get; set; }
        public string card_type { get; set; }
        public int total_card { get; set; }
        public int total_active_card { get; set; }
        public int total_deactive_card { get; set; }
    }

    public class UpdateMasterCompanyRequest
    {
        public string companyId { get; set; } = string.Empty;
        public string contactPerson { get; set; } = string.Empty;
        public string email { get; set; } = string.Empty;
        public string phone { get; set; } = string.Empty;
    }

    private byte[] BuildColoredExcelWithNpoi(List<MasterCompanyModel> masters)
    {
        IWorkbook workbook = new XSSFWorkbook();
        ISheet sheet = workbook.CreateSheet("Company Hierarchy");

        var headers = new[] {
            "Site Name", "Master Company Name", "Contact Person", "Email", "Phone", "Status",
            "Sub Company Name", "Sub Company Contact Person", "Sub Email", "Sub Company Contact/USER ID", "PIN",
            "Sub Status", "Applicable Card Type", "Password", "Card Limit", "Total Cards", "Total Paid", "Total Deducted", "Balance",
            "Card Name", "Card Type", "Plate No", "Owner Name", "Owner Phone", "Card Status", "Expiry Date", "Access Rights"
        };

        // Header Style
        var headerStyle = workbook.CreateCellStyle();
        headerStyle.FillForegroundColor = IndexedColors.Grey40Percent.Index;
        headerStyle.FillPattern = FillPattern.SolidForeground;
        var headerFont = workbook.CreateFont();
        headerFont.IsBold = true;
        headerFont.Color = IndexedColors.White.Index;
        headerStyle.SetFont(headerFont);

        // Master: Yellow
        var masterStyle = workbook.CreateCellStyle();
        masterStyle.FillForegroundColor = IndexedColors.Yellow.Index;
        masterStyle.FillPattern = FillPattern.SolidForeground;
        masterStyle.BorderBottom = masterStyle.BorderLeft = masterStyle.BorderRight = masterStyle.BorderTop = BorderStyle.Thin;
        var masterFont = workbook.CreateFont();
        masterFont.IsBold = true;
        masterStyle.SetFont(masterFont);

        // Sub: Green
        var subStyle = workbook.CreateCellStyle();
        subStyle.FillForegroundColor = IndexedColors.LightGreen.Index;
        subStyle.FillPattern = FillPattern.SolidForeground;
        subStyle.BorderBottom = subStyle.BorderLeft = subStyle.BorderRight = subStyle.BorderTop = BorderStyle.Thin;
        var subFont = workbook.CreateFont();
        subFont.IsItalic = true;
        subStyle.SetFont(subFont);

        // Card: no fill
        var cardStyle = workbook.CreateCellStyle();
        cardStyle.BorderBottom = cardStyle.BorderLeft = cardStyle.BorderRight = cardStyle.BorderTop = BorderStyle.Thin;

        // Write header
        var headerRow = sheet.CreateRow(0);
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = headerRow.CreateCell(i);
            cell.SetCellValue(headers[i]);
            cell.CellStyle = headerStyle;
        }

        int rowNum = 1;
        foreach (var master in masters)
        {
            var masterRow = sheet.CreateRow(rowNum++);

            masterRow.CreateCell(0).SetCellValue(master.Site_Name ?? "");
            masterRow.CreateCell(1).SetCellValue(master.car_parkers_company_name ?? "");
            masterRow.CreateCell(2).SetCellValue(master.car_parkers_contact_person ?? "");
            masterRow.CreateCell(3).SetCellValue(master.car_parkers_email ?? "");
            masterRow.CreateCell(4).SetCellValue(master.car_parkers_contact_number ?? "");
            masterRow.CreateCell(5).SetCellValue(master.car_parkers_status == "1" ? "Active" : "Inactive");

            for (int col = 0; col < headers.Length; col++)
            {
                if (masterRow.GetCell(col) == null) masterRow.CreateCell(col);
                masterRow.GetCell(col)!.CellStyle = masterStyle;
            }

            foreach (var sub in master.SubCompanies)
            {
                var subRow = sheet.CreateRow(rowNum++);

                subRow.CreateCell(6).SetCellValue(sub.car_parkers_sub_co_name ?? "");
                subRow.CreateCell(7).SetCellValue(sub.sub_co_car_parkers_contact_person ?? "");
                subRow.CreateCell(8).SetCellValue(sub.sub_co_car_parkers_email ?? "");
                subRow.CreateCell(9).SetCellValue(sub.sub_co_car_parkers_contact_number ?? "");
                subRow.CreateCell(10).SetCellValue(sub.sub_co_car_parkers_pin ?? "");
                subRow.CreateCell(11).SetCellValue(sub.car_parkers_status == "1" ? "Active" : "Inactive");
                subRow.CreateCell(12).SetCellValue(sub.applicable_card_type ?? "");
                subRow.CreateCell(13).SetCellValue(sub.card_parkers_company_password ?? "");
                subRow.CreateCell(14).SetCellValue(sub.Total_Card_Limit_Under_Sub_Co?.ToString() ?? "");
                subRow.CreateCell(15).SetCellValue(sub.Total_Cards_Under_Sub_Co?.ToString() ?? "");
                subRow.CreateCell(16).SetCellValue(sub.Total_Paid_Amount?.ToString() ?? "");
                subRow.CreateCell(17).SetCellValue(sub.Total_Deducted_Amount?.ToString() ?? "");
                subRow.CreateCell(18).SetCellValue(sub.Balance_Amount?.ToString() ?? "");

                for (int col = 0; col < headers.Length; col++)
                {
                    if (subRow.GetCell(col) == null) subRow.CreateCell(col);
                    subRow.GetCell(col)!.CellStyle = subStyle;
                }

                foreach (var card in sub.Cards)
                {
                    var cardRow = sheet.CreateRow(rowNum++);

                    cardRow.CreateCell(19).SetCellValue(card.cardname ?? "");
                    cardRow.CreateCell(20).SetCellValue(card.card_type_remark ?? "");
                    cardRow.CreateCell(21).SetCellValue(card.car_plate_no ?? "");
                    cardRow.CreateCell(22).SetCellValue(card.car_owner_name ?? "");
                    cardRow.CreateCell(23).SetCellValue(card.car_owner_contact_no ?? "");
                    cardRow.CreateCell(24).SetCellValue(card.status == "1" ? "Active" : "Inactive");
                    cardRow.CreateCell(25).SetCellValue(card.expiry_date?.ToString("dd-MMM-yyyy") ?? "");
                    cardRow.CreateCell(26).SetCellValue(card.access_rights ?? "");

                    for (int col = 0; col < headers.Length; col++)
                    {
                        if (cardRow.GetCell(col) == null) cardRow.CreateCell(col);
                        cardRow.GetCell(col)!.CellStyle = cardStyle;
                    }
                }
            }
            rowNum++;
        }

        for (int i = 0; i < headers.Length; i++)
            sheet.AutoSizeColumn(i);

        using var ms = new MemoryStream();
        workbook.Write(ms);
        return ms.ToArray();
    }

    public class CompanyEmailRequest
    {
        public string companyId { get; set; } = string.Empty;
        public string email { get; set; } = string.Empty;
        public string subject { get; set; } = string.Empty;
        public string body { get; set; } = string.Empty;
    }
}
