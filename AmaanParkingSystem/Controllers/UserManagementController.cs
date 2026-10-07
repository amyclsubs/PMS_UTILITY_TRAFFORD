using AmaanParkingSystem.Controllers;
using AmaanParkingSystem.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using System.Text;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

public class UserManagementController : BaseController
{
    public UserManagementController()
    {
    }

    private void FillDropdownViewbags(SqlConnection conn)
    {
        var sites = conn.Query("SELECT customer_id, site_name FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]").ToList();

        // Get sub companies with card type remark
        var subCompanies = conn.Query(@"
        SELECT 
            pc.car_parkers_sub_co_id,
            pc.car_parkers_sub_co_name,
            pc.car_parkers_company_id,
            pc.car_parkers_company_name,
            pc.customer_id,
            pc.applicable_card_type,
            ISNULL(cm.Remark, pc.applicable_card_type) as card_type_remark
        FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] pc
        LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_10_cards_master] cm 
            ON pc.applicable_card_type = cm.card_type
        WHERE pc.car_parkers_status = '1'
        ORDER BY pc.car_parkers_sub_co_name ASC
    ").ToList();

        var zones = conn.Query("SELECT zone_name FROM [AMAAN_PMS].[dbo].[db_tbl_04_zones]").ToList();

        ViewBag.Sites = new SelectList(sites, "customer_id", "site_name");
        ViewBag.SubCompanies = new SelectList(subCompanies, "car_parkers_sub_co_id", "car_parkers_sub_co_name");
        ViewBag.Zones = zones;
    }

    [HttpGet]
    public IActionResult CardRegistration(
    int? id = null,
    string sortColumn = null,
    string sortOrder = "asc",
    string filterCardType = null,
    int page = 1,
    int pageSize = 100,
    string searchTerm = null)

    {
        using var conn = new SqlConnection(GetDynamicConnectionString());
        FillDropdownViewbags(conn);

        var cardTypes = conn.Query(@"
        SELECT DISTINCT 
            card_type,
            Remark,
            ISNULL(Remark, card_type) as display_name
        FROM [AMAAN_PMS].[dbo].[db_tbl_10_cards_master]
        ORDER BY ISNULL(Remark, card_type) ASC
    ").ToList();

        ViewBag.CardTypes = cardTypes;
        ViewBag.FilterCardType = filterCardType;
        ViewBag.SearchTerm = searchTerm;

        string orderByClause = "ORDER BY ca.id ASC";

        if (!string.IsNullOrEmpty(sortColumn))
        {
            switch (sortColumn.ToLower())
            {
                case "companyname":
                    orderByClause = $"ORDER BY ca.car_parkers_company_name {sortOrder.ToUpper()}";
                    break;
                case "subconame":
                    orderByClause = $"ORDER BY ca.car_parkers_sub_co_name {sortOrder.ToUpper()}";
                    break;
                default:
                    orderByClause = "ORDER BY ca.id ASC";
                    break;
            }
        }

        // ✅ Build WHERE clause with MULTIPLE filters
        var whereClauses = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrEmpty(filterCardType))
        {
            whereClauses.Add("ca.card_type = @filterCardType");
            parameters.Add("filterCardType", filterCardType);
        }

        if (!string.IsNullOrEmpty(searchTerm))
        {
            whereClauses.Add(@"(
            ca.cardno LIKE @searchTerm OR 
            ca.cardname LIKE @searchTerm OR 
            ca.car_parkers_company_name LIKE @searchTerm OR 
            ca.car_parkers_sub_co_name LIKE @searchTerm OR 
            ca.car_owner_name LIKE @searchTerm OR 
            ca.car_owner_contact_no LIKE @searchTerm OR 
            ca.car_plate_no LIKE @searchTerm
        )");
            parameters.Add("searchTerm", "%" + searchTerm + "%");
        }

        string whereClause = whereClauses.Count > 0
            ? "WHERE " + string.Join(" AND ", whereClauses)
            : "";

        // ✅ Get TOTAL count for pagination
        int totalRecords = conn.ExecuteScalar<int>($@"
        SELECT COUNT(*)
        FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated] ca
        {whereClause}
    ", parameters);

        // ✅ Calculate pagination values
        int totalPages = (int)Math.Ceiling(totalRecords / (double)pageSize);
        int skip = (page - 1) * pageSize;

        ViewBag.CurrentPage = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalRecords = totalRecords;
        ViewBag.PageSize = pageSize;

        // ✅ FIXED: Add pagination parameters separately
        parameters.Add("skip", skip);
        parameters.Add("pageSize", pageSize);

        // ✅ Get paginated card list WITH OFFSET-FETCH
        var cardList = conn.Query<CardRegistrationModel>($@"
        SELECT 
            ca.*,
            ISNULL(cm.Remark, ca.card_type) as card_type_remark
        FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated] ca
        LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_10_cards_master] cm 
            ON ca.card_type = cm.card_type
        {whereClause}
        {orderByClause}
        OFFSET @skip ROWS
        FETCH NEXT @pageSize ROWS ONLY
    ", parameters).ToList();

        ViewBag.CardList = cardList;
        ViewBag.SortColumn = sortColumn;
        ViewBag.SortOrder = sortOrder;

        var model = new CardRegistrationModel();
        bool isEdit = false;

        if (id.HasValue && id.Value > 0)
        {
            model = conn.QuerySingleOrDefault<CardRegistrationModel>(@"
            SELECT 
                ca.*,
                ISNULL(cm.Remark, ca.card_type) as card_type_remark
            FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated] ca
            LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_10_cards_master] cm 
                ON ca.card_type = cm.card_type
            WHERE ca.id = @id
        ", new { id = id.Value });

            if (model != null)
            {
                isEdit = true;
            }
            else
            {
                TempData["ErrorMessage"] = "Card not found!";
                return RedirectToAction("CardRegistration");
            }
        }
        var allPlateNumbers = cardList
            .Where(c => !string.IsNullOrEmpty(c.car_plate_no))
            .Select(c => c.car_plate_no)
            .ToList();

        ViewBag.DuplicatePlateNumbers = allPlateNumbers
            .GroupBy(p => p)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        ViewBag.IsEdit = isEdit;
        return View(model);
    }

    [HttpGet]
    public JsonResult GetSubCompanyDetails(string subCoId)
    {
        using var conn = new SqlConnection(GetDynamicConnectionString());

        var subCompanyDetails = conn.QueryFirstOrDefault(@"
        SELECT 
            pc.customer_id,
            pc.car_parkers_company_id,
            pc.car_parkers_company_name,
            pc.car_parkers_sub_co_id,
            pc.car_parkers_sub_co_name,
            pc.applicable_card_type,
            ISNULL(cm.Remark, pc.applicable_card_type) as card_type_remark
        FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] pc
        LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_10_cards_master] cm 
            ON pc.applicable_card_type = cm.card_type
        WHERE pc.car_parkers_sub_co_id = @subCoId
    ", new { subCoId });

        if (subCompanyDetails == null)
            return Json(new { success = false });

        return Json(new
        {
            success = true,
            customer_id = subCompanyDetails.customer_id,
            company_id = subCompanyDetails.car_parkers_company_id,
            company_name = subCompanyDetails.car_parkers_company_name,
            sub_co_id = subCompanyDetails.car_parkers_sub_co_id,
            sub_co_name = subCompanyDetails.car_parkers_sub_co_name,
            card_type = subCompanyDetails.applicable_card_type,
            card_type_remark = subCompanyDetails.card_type_remark
        });
    }
    [HttpGet]
    public JsonResult GetCardTypeFromSuffix(string suffix)
    {
        using var conn = new SqlConnection(GetDynamicConnectionString());

        // Query the cards master table to get card type based on suffix
        var cardType = conn.QueryFirstOrDefault<string>(@"
        SELECT TOP 1 card_type
        FROM [AMAAN_PMS].[dbo].[db_tbl_10_cards_master]
        WHERE card_abbr = @suffix OR card_suffix = @suffix
        ORDER BY id ASC
    ", new { suffix });

        return Json(new
        {
            success = cardType != null,
            card_type = cardType
        });
    }

    [HttpGet]
    public IActionResult ExportCardsToExcel(string filterCardType = null, string sortColumn = null, string sortOrder = "asc")
    {
        using var conn = new SqlConnection(GetDynamicConnectionString());

        // Build dynamic ORDER BY clause
        string orderByClause = "ORDER BY ca.id ASC";

        if (!string.IsNullOrEmpty(sortColumn))
        {
            switch (sortColumn.ToLower())
            {
                case "companyname":
                    orderByClause = $"ORDER BY ca.car_parkers_company_name {sortOrder.ToUpper()}";
                    break;
                case "subconame":
                    orderByClause = $"ORDER BY ca.car_parkers_sub_co_name {sortOrder.ToUpper()}";
                    break;
                default:
                    orderByClause = "ORDER BY ca.id ASC";
                    break;
            }
        }

        // Build WHERE clause for card type filter
        string whereClause = "";
        object queryParams = null;

        if (!string.IsNullOrEmpty(filterCardType))
        {
            whereClause = "WHERE ca.card_type = @filterCardType";
            queryParams = new { filterCardType };
        }

        // Get card list WITH card type remarks and filter
        var cardList = conn.Query<CardRegistrationModel>($@"
        SELECT 
            ca.*,
            ISNULL(cm.Remark, ca.card_type) as card_type_remark
        FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated] ca
        LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_10_cards_master] cm 
            ON ca.card_type = cm.card_type
        {whereClause}
        {orderByClause}
    ", queryParams ?? new { }).ToList();

        // Find duplicate Owner Contact values
        var ownerContactGroups = cardList
            .Where(c => !string.IsNullOrEmpty(c.car_owner_contact_no))
            .GroupBy(c => c.car_owner_contact_no)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        // Create Excel workbook using NPOI
        var workbook = new XSSFWorkbook();
        var sheet = workbook.CreateSheet("Cards Allocated");

        // Create styles
        var headerStyle = workbook.CreateCellStyle();
        var headerFont = workbook.CreateFont();
        headerFont.IsBold = true;
        headerFont.Color = IndexedColors.White.Index;
        headerFont.FontHeightInPoints = 11;
        headerStyle.SetFont(headerFont);
        headerStyle.FillForegroundColor = IndexedColors.Blue.Index;
        headerStyle.FillPattern = FillPattern.SolidForeground;
        headerStyle.Alignment = HorizontalAlignment.Center;
        headerStyle.VerticalAlignment = VerticalAlignment.Center;

        var duplicateStyle = workbook.CreateCellStyle();
        var duplicateFont = workbook.CreateFont();
        duplicateFont.IsBold = true;
        duplicateFont.Color = IndexedColors.Red.Index;
        duplicateStyle.SetFont(duplicateFont);

        var alternateRowStyle = workbook.CreateCellStyle();
        alternateRowStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index;
        alternateRowStyle.FillPattern = FillPattern.SolidForeground;

        // Define headers
        string[] headers = new[]
        {
        "ID", "Customer ID", "Company ID", "Company Name", "Sub Co ID", "Sub Co Name",
        "Card No", "Card Name", "Card Type", "Plate No", "Owner Name", "Owner Contact",
        "Application Form", "Fee Schedule ID", "Expiry Date", "Access Rights", "Anti Passback",
        "Grouped", "Group Type", "Group ID", "Master/Sub Card", "Balance", "Status",
        "Created By", "Created On", "Updated By", "Updated On"
    };

        // Create header row
        var headerRow = sheet.CreateRow(0);
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = headerRow.CreateCell(i);
            cell.SetCellValue(headers[i]);
            cell.CellStyle = headerStyle;
        }

        // Add data rows
        int rowNum = 1;
        foreach (var card in cardList)
        {
            var row = sheet.CreateRow(rowNum);

            // Add data to cells FIRST
            row.CreateCell(0).SetCellValue(card.id);
            row.CreateCell(1).SetCellValue(card.customer_id ?? "");
            row.CreateCell(2).SetCellValue(card.car_parkers_company_id ?? "");
            row.CreateCell(3).SetCellValue(card.car_parkers_company_name ?? "");
            row.CreateCell(4).SetCellValue(card.car_parkers_sub_co_id ?? "");
            row.CreateCell(5).SetCellValue(card.car_parkers_sub_co_name ?? "");
            row.CreateCell(6).SetCellValue(card.cardno ?? "");
            row.CreateCell(7).SetCellValue(card.cardname ?? "");
            row.CreateCell(8).SetCellValue(card.card_type_remark ?? "");
            row.CreateCell(9).SetCellValue(card.car_plate_no ?? "");
            row.CreateCell(10).SetCellValue(card.car_owner_name ?? "");

            // Owner Contact - with duplicate highlighting
            var ownerContactCell = row.CreateCell(11);
            ownerContactCell.SetCellValue(card.car_owner_contact_no ?? "");

            // Check if duplicate and apply red bold style
            bool isDuplicate = !string.IsNullOrEmpty(card.car_owner_contact_no) &&
                               ownerContactGroups.Contains(card.car_owner_contact_no);

            row.CreateCell(12).SetCellValue(card.applicationform ?? "");
            row.CreateCell(13).SetCellValue(card.fee_schedule_id ?? "");

            // Format expiry_date as 24-JAN-2026
            var expiryCell = row.CreateCell(14);
            if (!string.IsNullOrEmpty(card.expiry_date) && DateTime.TryParse(card.expiry_date, out DateTime expiryDate))
            {
                expiryCell.SetCellValue(expiryDate.ToString("dd-MMM-yyyy").ToUpper());
            }
            else
            {
                expiryCell.SetCellValue(card.expiry_date ?? "");
            }

            row.CreateCell(15).SetCellValue(card.access_rights ?? "");
            row.CreateCell(16).SetCellValue(card.antipassback ?? "");
            row.CreateCell(17).SetCellValue(card.grouped ?? "");
            row.CreateCell(18).SetCellValue(card.group_type ?? "");
            row.CreateCell(19).SetCellValue(card.group_id ?? "");
            row.CreateCell(20).SetCellValue(card.master_or_sub_card ?? "");
            row.CreateCell(21).SetCellValue(card.balance ?? "");
            row.CreateCell(22).SetCellValue(card.status == "1" ? "Active" : "Inactive");
            row.CreateCell(23).SetCellValue(card.created_by ?? "");

            // Format created_on as 24-JAN-2026 12:30:45
            var createdCell = row.CreateCell(24);
            if (!string.IsNullOrEmpty(card.created_on) && DateTime.TryParse(card.created_on, out DateTime createdDate))
            {
                createdCell.SetCellValue(createdDate.ToString("dd-MMM-yyyy HH:mm:ss").ToUpper());
            }
            else
            {
                createdCell.SetCellValue(card.created_on ?? "");
            }

            row.CreateCell(25).SetCellValue(card.updated_by ?? "");

            // Format updated_on as 24-JAN-2026 12:30:45
            var updatedCell = row.CreateCell(26);
            if (!string.IsNullOrEmpty(card.updated_on) && DateTime.TryParse(card.updated_on, out DateTime updatedDate))
            {
                updatedCell.SetCellValue(updatedDate.ToString("dd-MMM-yyyy HH:mm:ss").ToUpper());
            }
            else
            {
                updatedCell.SetCellValue(card.updated_on ?? "");
            }

            // Apply alternate row style AFTER creating all cells (but NOT to duplicate cell)
            if (rowNum % 2 == 0)
            {
                for (int i = 0; i < headers.Length; i++)
                {
                    var cell = row.GetCell(i);
                    if (cell != null)
                    {
                        // Don't override duplicate style for Owner Contact column
                        if (i == 11 && isDuplicate)
                        {
                            // Create combined style: alternate row color + red bold text
                            var combinedStyle = workbook.CreateCellStyle();
                            combinedStyle.CloneStyleFrom(alternateRowStyle);
                            var redFont = workbook.CreateFont();
                            redFont.IsBold = true;
                            redFont.Color = IndexedColors.Red.Index;
                            combinedStyle.SetFont(redFont);
                            cell.CellStyle = combinedStyle;
                        }
                        else
                        {
                            cell.CellStyle = alternateRowStyle;
                        }
                    }
                }
            }
            else if (isDuplicate)
            {
                // Apply duplicate style to Owner Contact cell on odd rows
                ownerContactCell.CellStyle = duplicateStyle;
            }

            rowNum++;
        }

        // Auto-size columns
        for (int i = 0; i < headers.Length; i++)
        {
            sheet.AutoSizeColumn(i);
            // Set max width to prevent extremely wide columns
            if (sheet.GetColumnWidth(i) > 15000)
                sheet.SetColumnWidth(i, 15000);
        }

        // Freeze header row
        sheet.CreateFreezePane(0, 1);

        // Write to byte array directly
        byte[] fileBytes;
        using (var stream = new MemoryStream())
        {
            workbook.Write(stream, true);
            fileBytes = stream.ToArray();
        }

        // Close workbook
        workbook.Close();

        string fileName = $"Cards_Allocated_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";

        return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }


    [HttpPost]
    public IActionResult AddOrEditCard(CardRegistrationModel model, string[] zoneCheckboxes)
    {
        using (var conn = new SqlConnection(GetDynamicConnectionString()))
        {
            var currentUser = GetLoggedInUser();

            // ✅ DUPLICATE CHECK: Card Number (cardno)
            if (!string.IsNullOrWhiteSpace(model.cardno))
            {
                var dupCardNoSql = model.id > 0
                    ? @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated]
                    WHERE LTRIM(RTRIM(LOWER(cardno))) = LTRIM(RTRIM(LOWER(@cardno))) AND id != @id"
                    : @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated]
                    WHERE LTRIM(RTRIM(LOWER(cardno))) = LTRIM(RTRIM(LOWER(@cardno)))";

                var cardNoExists = conn.ExecuteScalar<int>(
                    dupCardNoSql, new { model.cardno, model.id }) > 0;

                if (cardNoExists)
                {
                    TempData["ErrorMessage"] = $"Card Number '{model.cardno}' already exists! Please use a different card number.";
                    if (model.id > 0)
                        return RedirectToAction("CardRegistration", new { id = model.id });
                    else
                        return RedirectToAction("CardRegistration");
                }
            }

            // ✅ DUPLICATE CHECK: Card Name (cardname)
            if (!string.IsNullOrWhiteSpace(model.cardname))
            {
                var dupCardNameSql = model.id > 0
                    ? @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated]
                    WHERE LTRIM(RTRIM(LOWER(cardname))) = LTRIM(RTRIM(LOWER(@cardname))) AND id != @id"
                    : @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated]
                    WHERE LTRIM(RTRIM(LOWER(cardname))) = LTRIM(RTRIM(LOWER(@cardname)))";

                var cardNameExists = conn.ExecuteScalar<int>(
                    dupCardNameSql, new { model.cardname, model.id }) > 0;

                if (cardNameExists)
                {
                    TempData["ErrorMessage"] = $"Card Name '{model.cardname}' already exists! Please use a different card name.";
                    if (model.id > 0)
                        return RedirectToAction("CardRegistration", new { id = model.id });
                    else
                        return RedirectToAction("CardRegistration");
                }
            }

            var subCompanyInfo = conn.QueryFirstOrDefault(@"
            SELECT customer_id, car_parkers_company_id, car_parkers_company_name, 
                   car_parkers_sub_co_id, car_parkers_sub_co_name, applicable_card_type 
            FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] 
            WHERE car_parkers_sub_co_id = @subCoId", new { subCoId = model.car_parkers_sub_co_id });

            if (subCompanyInfo == null)
            {
                TempData["ErrorMessage"] = "Sub Company not found!";
                return RedirectToAction("CardRegistration", new { id = model.id });
            }

            // Binary Rights Logic using 'conn'
            var zonesList = conn.Query("SELECT zone_name FROM [AMAAN_PMS].[dbo].[db_tbl_04_zones]").ToList();
            var binaryRights = new StringBuilder();
            foreach (var z in zonesList)
            {
                binaryRights.Append((zoneCheckboxes != null && zoneCheckboxes.Contains((string)z.zone_name)) ? "1" : "0");
            }
            string assignedRights = binaryRights.ToString();

            // Mapping Card Type
            string cardType = subCompanyInfo.applicable_card_type;
            var cMapping = new Dictionary<string, string> { { "FOC_PER_UNIT", "SEASONAL" }, { "PARKING_OWNER", "VIP" } };
            if (cMapping.ContainsKey(cardType)) cardType = cMapping[cardType];

            if (model.id > 0)
            {
                conn.Execute(@"
                UPDATE [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated]
                SET customer_id=@customer_id, car_parkers_company_id=@car_parkers_company_id, 
                    car_parkers_company_name=@car_parkers_company_name, car_parkers_sub_co_id=@car_parkers_sub_co_id, 
                    car_parkers_sub_co_name=@car_parkers_sub_co_name, cardno=@cardno, cardname=@cardname, 
                    car_owner_name=@car_owner_name, car_owner_contact_no=@car_owner_contact_no, 
                    car_plate_no=@car_plate_no, card_type=@card_type, status=@status, 
                    fee_schedule_id=@fee_schedule_id, applicationform=@applicationform, 
                    expiry_date=@expiry_date, access_rights=@access_rights, antipassback=@antipassback, 
                    grouped=@grouped, updated_by=@updated_by, updated_on=GETDATE()
                WHERE id=@id", new
                {
                    model.id,
                    customer_id = subCompanyInfo.customer_id,
                    car_parkers_company_id = subCompanyInfo.car_parkers_company_id,
                    car_parkers_company_name = subCompanyInfo.car_parkers_company_name,
                    car_parkers_sub_co_id = subCompanyInfo.car_parkers_sub_co_id,
                    car_parkers_sub_co_name = subCompanyInfo.car_parkers_sub_co_name,
                    model.cardno,
                    model.cardname,
                    model.car_owner_name,
                    model.car_owner_contact_no,
                    model.car_plate_no,
                    card_type = cardType,
                    model.status,
                    model.fee_schedule_id,
                    model.applicationform,
                    model.expiry_date,
                    access_rights = assignedRights,
                    model.antipassback,
                    model.grouped,
                    updated_by = currentUser
                });
            }
            else
            {
                conn.Execute(@"
                INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated]
                (customer_id, car_parkers_company_id, car_parkers_company_name, car_parkers_sub_co_id, car_parkers_sub_co_name, 
                 cardno, cardname, car_owner_name, car_owner_contact_no, car_plate_no, card_type, status, 
                 fee_schedule_id, applicationform, expiry_date, access_rights, antipassback, grouped, created_by, created_on)
                VALUES (@customer_id, @company_id, @company_name, @sub_id, @sub_name, @cardno, @cardname, @owner, @contact, 
                        @plate, @ctype, @status, @fees, @app, @expiry, @rights, @apb, @grp, @user, GETDATE())", new
                {
                    customer_id = subCompanyInfo.customer_id,
                    company_id = subCompanyInfo.car_parkers_company_id,
                    company_name = subCompanyInfo.car_parkers_company_name,
                    sub_id = subCompanyInfo.car_parkers_sub_co_id,
                    sub_name = subCompanyInfo.car_parkers_sub_co_name,
                    model.cardno,
                    model.cardname,
                    owner = model.car_owner_name,
                    contact = model.car_owner_contact_no,
                    plate = model.car_plate_no,
                    ctype = cardType,
                    model.status,
                    fees = model.fee_schedule_id,
                    app = model.applicationform,
                    expiry = model.expiry_date,
                    rights = assignedRights,
                    apb = model.antipassback,
                    grp = model.grouped,
                    user = currentUser
                });
            }
            return RedirectToAction("CardRegistration");
        }
    }
}
