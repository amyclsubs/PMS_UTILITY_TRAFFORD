using AmaanParkingSystem.Models.UserManagement;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.IO;


namespace AmaanParkingSystem.Controllers
{
    public class ParkerCompanyController : BaseController
    {
        public ParkerCompanyController()
        {
        }

        // Get card type abbreviation - Generate from card_type since there's no abbr column
        private string GetCardTypeAbbreviation(string cardType)
        {
            if (string.IsNullOrEmpty(cardType))
                return "";

            // Generate abbreviation based on card type
            return cardType.ToUpper() switch
            {
                "FOC_PER_UNIT" => "FOC",
                "FOC PER UNIT" => "FOC",
                "DAILY ACCESS" => "DA",
                "MONTHLY" => "MON",
                "YEARLY" => "YR",
                "VISITOR" => "VIS",
                "STAFF" => "STF",
                "PERMANENT" => "PER",
                "TEMPORARY" => "TEMP",
                _ => cardType.Replace(" ", "").Replace("_", "").Substring(0, Math.Min(3, cardType.Replace(" ", "").Replace("_", "").Length)).ToUpper()
            };
        }

        [HttpGet]
        public IActionResult ParkerCompanyRegistration(string sortColumn = null, string sortOrder = "asc", string filterCardType = null)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            // Get card types for filter dropdown
            var cardTypes = conn.Query<dynamic>(@"
        SELECT DISTINCT 
            card_type,
            Remark,
            ISNULL(Remark, card_type) as display_name                                                     
        FROM [AMAAN_PMS].[dbo].[db_tbl_10_cards_master]
        ORDER BY ISNULL(Remark, card_type) ASC
    ").ToList();

            ViewBag.CardTypes = new SelectList(cardTypes, "Remark", "display_name");
            ViewBag.FilterCardType = filterCardType;

            // Get parent companies
            var parentCompanies = conn.Query<dynamic>(@"
        SELECT car_parkers_company_id, car_parkers_company_name 
        FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master]
        WHERE car_parkers_status = '1'
        ORDER BY car_parkers_company_name ASC
    ").ToList();
            ViewBag.ParentCompanies = new SelectList(parentCompanies, "car_parkers_company_id", "car_parkers_company_name");

            // Build dynamic ORDER BY clause
            string orderByClause = "ORDER BY id ASC";

            if (!string.IsNullOrEmpty(sortColumn))
            {
                switch (sortColumn.ToLower())
                {
                    case "companyname":
                        orderByClause = $"ORDER BY car_parkers_company_name {sortOrder.ToUpper()}";
                        break;
                    case "subconame":
                        orderByClause = $"ORDER BY car_parkers_sub_co_name {sortOrder.ToUpper()}";
                        break;
                    default:
                        orderByClause = "ORDER BY id ASC";
                        break;
                }
            }

            // Build WHERE clause for card type filter
            string whereClause = "";
            object queryParams = null;

            if (!string.IsNullOrEmpty(filterCardType))
            {
                // Get the Remark value for the selected card_type
                var cardTypeRemark = conn.QueryFirstOrDefault<string>(@"
        SELECT TOP 1 Remark 
        FROM [AMAAN_PMS].[dbo].[db_tbl_10_cards_master]
        WHERE card_type = @filterCardType
    ", new { filterCardType });

                // Use the Remark value if it exists, otherwise use the card_type itself
                var filterValue = !string.IsNullOrEmpty(cardTypeRemark) ? cardTypeRemark : filterCardType;

                whereClause = "WHERE applicable_card_type = @filterValue";
                queryParams = new { filterValue };
            }

            var companyList = conn.Query<ParkerCompanyModel>($@"
    SELECT * FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] 
    {whereClause}
    {orderByClause}
", queryParams ?? new { }).ToList();

            ViewBag.CompanyList = companyList;
            ViewBag.SortColumn = sortColumn;
            ViewBag.SortOrder = sortOrder;

            var newModel = new ParkerCompanyModel();
            return View("~/Views/UserManagement/ParkerCompanyRegistration.cshtml", newModel);
        }

        [HttpGet]
        public IActionResult EditParkerCompany(int id, string sortColumn = null, string sortOrder = "asc", string filterCardType = null)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            // Get card types for filter dropdown
            var cardTypes = conn.Query<dynamic>(@"
        SELECT DISTINCT 
            card_type,
            Remark,
            ISNULL(Remark, card_type) as display_name
        FROM [AMAAN_PMS].[dbo].[db_tbl_10_cards_master]
        ORDER BY ISNULL(Remark, card_type) ASC
    ").ToList();

            ViewBag.CardTypes = new SelectList(cardTypes, "Remark", "display_name");
            ViewBag.FilterCardType = filterCardType;

            // Get parent companies
            var parentCompanies = conn.Query<dynamic>(@"
        SELECT car_parkers_company_id, car_parkers_company_name 
        FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master]
        WHERE car_parkers_status = '1'
        ORDER BY car_parkers_company_name ASC
    ").ToList();
            ViewBag.ParentCompanies = new SelectList(parentCompanies, "car_parkers_company_id", "car_parkers_company_name");

            var company = conn.QuerySingleOrDefault<ParkerCompanyModel>(@"
        SELECT * FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] WHERE id = @id
    ", new { id });

            if (company == null)
                return RedirectToAction("ParkerCompanyRegistration");
            // Set the selected card type value
            ViewBag.SelectedCardType = company.applicable_card_type;

            // Build dynamic ORDER BY clause
            string orderByClause = "ORDER BY id ASC";

            if (!string.IsNullOrEmpty(sortColumn))
            {
                switch (sortColumn.ToLower())
                {
                    case "companyname":
                        orderByClause = $"ORDER BY car_parkers_company_name {sortOrder.ToUpper()}";
                        break;
                    case "subconame":
                        orderByClause = $"ORDER BY car_parkers_sub_co_name {sortOrder.ToUpper()}";
                        break;
                    default:
                        orderByClause = "ORDER BY id ASC";
                        break;
                }
            }

            // Build WHERE clause for card type filter
            string whereClause = "";
            object queryParams = null;

            if (!string.IsNullOrEmpty(filterCardType))
            {
                whereClause = "WHERE applicable_card_type = @filterCardType";
                queryParams = new { filterCardType };
            }

            var companyList = conn.Query<ParkerCompanyModel>($@"
        SELECT * FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] 
        {whereClause}
        {orderByClause}
    ", queryParams ?? new { }).ToList();

            ViewBag.CompanyList = companyList;
            ViewBag.SortColumn = sortColumn;
            ViewBag.SortOrder = sortOrder;
            ViewBag.IsEdit = true;

            return View("~/Views/UserManagement/ParkerCompanyRegistration.cshtml", company);
        }

        [HttpGet]
        public IActionResult ExportParkerCompaniesToExcel(string sortColumn = null, string sortOrder = "asc", string filterCardType = null)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            // Build dynamic ORDER BY clause
            string orderByClause = "ORDER BY id ASC";

            if (!string.IsNullOrEmpty(sortColumn))
            {
                switch (sortColumn.ToLower())
                {
                    case "companyname":
                        orderByClause = $"ORDER BY car_parkers_company_name {sortOrder.ToUpper()}";
                        break;
                    case "subconame":
                        orderByClause = $"ORDER BY car_parkers_sub_co_name {sortOrder.ToUpper()}";
                        break;
                    default:
                        orderByClause = "ORDER BY id ASC";
                        break;
                }
            }

            // Build WHERE clause for card type filter
            string whereClause = "";
            object queryParams = null;

            if (!string.IsNullOrEmpty(filterCardType))
            {
                whereClause = "WHERE applicable_card_type = @filterCardType";
                queryParams = new { filterCardType };
            }

            var companyList = conn.Query<ParkerCompanyModel>($@"
        SELECT * FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] 
        {whereClause}
        {orderByClause}
    ", queryParams ?? new { }).ToList();

            // Find duplicate Emails and Contact Numbers
            var duplicateEmails = companyList
                .Where(c => !string.IsNullOrEmpty(c.sub_co_car_parkers_email))
                .GroupBy(c => c.sub_co_car_parkers_email)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            var duplicateContacts = companyList
                .Where(c => !string.IsNullOrEmpty(c.sub_co_car_parkers_contact_number))
                .GroupBy(c => c.sub_co_car_parkers_contact_number)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            // Create Excel workbook using NPOI
            var workbook = new XSSFWorkbook();
            var sheet = workbook.CreateSheet("Parker Companies");

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
        "Card Type", "Contact Person", "Email", "Contact Number", "Address", "PIN", "VAT",
        "Status", "Created By", "Created On", "Updated By", "Updated On",
        "Total Paid", "Total Deducted", "Balance", "Total Cards", "Card Limit", "Password"
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
            foreach (var c in companyList)
            {
                var row = sheet.CreateRow(rowNum);

                row.CreateCell(0).SetCellValue(c.id);

                var cell1 = row.CreateCell(1);
                if (!string.IsNullOrEmpty(c.customer_id)) cell1.SetCellValue(c.customer_id);

                var cell2 = row.CreateCell(2);
                if (!string.IsNullOrEmpty(c.car_parkers_company_id)) cell2.SetCellValue(c.car_parkers_company_id);

                var cell3 = row.CreateCell(3);
                if (!string.IsNullOrEmpty(c.car_parkers_company_name)) cell3.SetCellValue(c.car_parkers_company_name);

                var cell4 = row.CreateCell(4);
                if (!string.IsNullOrEmpty(c.car_parkers_sub_co_id)) cell4.SetCellValue(c.car_parkers_sub_co_id);

                var cell5 = row.CreateCell(5);
                if (!string.IsNullOrEmpty(c.car_parkers_sub_co_name)) cell5.SetCellValue(c.car_parkers_sub_co_name);

                var cell6 = row.CreateCell(6);
                if (!string.IsNullOrEmpty(c.applicable_card_type)) cell6.SetCellValue(c.applicable_card_type);

                var cell7 = row.CreateCell(7);
                if (!string.IsNullOrEmpty(c.sub_co_car_parkers_contact_person)) cell7.SetCellValue(c.sub_co_car_parkers_contact_person);

                // Email - with duplicate highlighting
                var emailCell = row.CreateCell(8);
                if (!string.IsNullOrEmpty(c.sub_co_car_parkers_email))
                {
                    emailCell.SetCellValue(c.sub_co_car_parkers_email);
                    if (duplicateEmails.Contains(c.sub_co_car_parkers_email))
                    {
                        emailCell.CellStyle = duplicateStyle;
                    }
                }

                // Contact Number - with duplicate highlighting
                var contactCell = row.CreateCell(9);
                if (!string.IsNullOrEmpty(c.sub_co_car_parkers_contact_number))
                {
                    contactCell.SetCellValue(c.sub_co_car_parkers_contact_number);
                    if (duplicateContacts.Contains(c.sub_co_car_parkers_contact_number))
                    {
                        contactCell.CellStyle = duplicateStyle;
                    }
                }

                var cell10 = row.CreateCell(10);
                if (!string.IsNullOrEmpty(c.sub_co_car_parkers_address)) cell10.SetCellValue(c.sub_co_car_parkers_address);

                var cell11 = row.CreateCell(11);
                if (!string.IsNullOrEmpty(c.sub_co_car_parkers_pin)) cell11.SetCellValue(c.sub_co_car_parkers_pin);

                var cell12 = row.CreateCell(12);
                if (!string.IsNullOrEmpty(c.sub_co_car_parkers_vat)) cell12.SetCellValue(c.sub_co_car_parkers_vat);

                var cell13 = row.CreateCell(13);
                cell13.SetCellValue(c.car_parkers_status == "1" ? "Active" : "Inactive");

                var cell14 = row.CreateCell(14);
                if (!string.IsNullOrEmpty(c.created_by)) cell14.SetCellValue(c.created_by);

                var cell15 = row.CreateCell(15);
                if (c.created_on.HasValue)
                    cell15.SetCellValue(c.created_on.Value.ToString("dd-MMM-yyyy HH:mm:ss").ToUpper());

                var cell16 = row.CreateCell(16);
                if (!string.IsNullOrEmpty(c.updated_by)) cell16.SetCellValue(c.updated_by);

                var cell17 = row.CreateCell(17);
                if (c.updated_on.HasValue)
                    cell17.SetCellValue(c.updated_on.Value.ToString("dd-MMM-yyyy HH:mm:ss").ToUpper());

                var cell18 = row.CreateCell(18);
                if (c.Total_Paid_Amount.HasValue)
                    cell18.SetCellValue((double)c.Total_Paid_Amount.Value);

                var cell19 = row.CreateCell(19);
                if (c.Total_Deducted_Amount.HasValue)
                    cell19.SetCellValue((double)c.Total_Deducted_Amount.Value);

                var cell20 = row.CreateCell(20);
                if (c.Balance_Amount.HasValue)
                    cell20.SetCellValue((double)c.Balance_Amount.Value);

                var cell21 = row.CreateCell(21);
                if (!string.IsNullOrEmpty(c.Total_Cards_Under_Sub_Co))
                {
                    if (int.TryParse(c.Total_Cards_Under_Sub_Co, out int totalCards))
                        cell21.SetCellValue(totalCards);
                }

                var cell22 = row.CreateCell(22);
                if (!string.IsNullOrEmpty(c.Total_Card_Limit_Under_Sub_Co))
                {
                    if (int.TryParse(c.Total_Card_Limit_Under_Sub_Co, out int cardLimit))
                        cell22.SetCellValue(cardLimit);
                }

                var cell23 = row.CreateCell(23);
                cell23.SetCellValue(string.IsNullOrEmpty(c.card_parkers_company_password) ? "-" : "****");

                // Apply alternate row style (but preserve duplicate styles)
                if (rowNum % 2 == 0)
                {
                    for (int i = 0; i < headers.Length; i++)
                    {
                        var cell = row.GetCell(i);
                        if (cell != null && cell.CellStyle != duplicateStyle)
                        {
                            var newStyle = workbook.CreateCellStyle();
                            newStyle.CloneStyleFrom(alternateRowStyle);
                            cell.CellStyle = newStyle;
                        }
                    }
                }

                rowNum++;
            }

            // Auto-size columns
            for (int i = 0; i < headers.Length; i++)
            {
                sheet.AutoSizeColumn(i);
                if (sheet.GetColumnWidth(i) > 15000)
                    sheet.SetColumnWidth(i, 15000);
            }

            // Freeze header row
            sheet.CreateFreezePane(0, 1);

            // Write to byte array
            byte[] fileBytes;
            using (var exportStream = new MemoryStream())
            {
                workbook.Write(exportStream, true);
                fileBytes = exportStream.ToArray();
            }

            workbook.Close();

            string fileName = $"Parker_Companies_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";

            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        // New endpoint to get shortcode for selected card type
        [HttpGet]
        public JsonResult GetCardTypeShortCode(string cardType)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            var shortCode = conn.QueryFirstOrDefault<string>(@"
        SELECT TOP 1 Sub_Company_Shortcode 
        FROM [AMAAN_PMS].[dbo].[db_tbl_10_cards_master]
        WHERE Remark = @cardType
    ", new { cardType });

            return Json(new { shortCode = shortCode ?? "" });
        }

        [HttpGet]
        public JsonResult CheckSubCompanyExists(string companyId, string cardType)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            // Get the shortcode from cards master table
            var shortCode = conn.QueryFirstOrDefault<string>(@"
        SELECT TOP 1 Sub_Company_Shortcode 
        FROM [AMAAN_PMS].[dbo].[db_tbl_10_cards_master]
        WHERE Remark = @cardType
    ", new { cardType });

            if (string.IsNullOrEmpty(shortCode))
            {
                return Json(new { exists = false, nextCounter = 1 });
            }

            var baseSubCompanyId = $"{companyId}_{shortCode}";

            // Count all sub companies with this pattern
            var existingCount = conn.QueryFirstOrDefault<int>(@"
        SELECT COUNT(*) 
        FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
        WHERE car_parkers_sub_co_id = @baseId 
           OR car_parkers_sub_co_id LIKE @pattern
    ", new
            {
                baseId = baseSubCompanyId,
                pattern = baseSubCompanyId + "_%"
            });

            var exists = existingCount > 0;
            var nextCounter = existingCount + 1;

            return Json(new { exists = exists, nextCounter = nextCounter });
        }

        [HttpGet]
        public JsonResult CheckContactNumberExists(string contactNumber, int id = 0)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            var sql = id > 0
                ? @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] 
            WHERE sub_co_car_parkers_contact_number = @contactNumber AND id != @id"
                : @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] 
            WHERE sub_co_car_parkers_contact_number = @contactNumber";

            var exists = conn.ExecuteScalar<int>(sql, new { contactNumber, id }) > 0;

            return Json(new { exists });
        }


        // AJAX endpoint to generate sub company ID
        [HttpPost]
        public IActionResult AddOrEditParkerCompany(ParkerCompanyModel model, string NegBalAllowed, string CardLimitInput)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());
            var currentUser = GetLoggedInUser();

            // ✅ DUPLICATE CHECK: Contact Number
            if (!string.IsNullOrWhiteSpace(model.sub_co_car_parkers_contact_number))
            {
                var duplicateCheckSql = model.id > 0
                    ? @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] 
                WHERE sub_co_car_parkers_contact_number = @contactNumber 
                AND id != @id"
                    : @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] 
                WHERE sub_co_car_parkers_contact_number = @contactNumber";

                var duplicateExists = conn.ExecuteScalar<int>(duplicateCheckSql, new
                {
                    contactNumber = model.sub_co_car_parkers_contact_number,
                    id = model.id
                }) > 0;

                if (duplicateExists)
                {
                    TempData["ErrorMessage"] = $"Contact Number '{model.sub_co_car_parkers_contact_number}' already exists! Please use a different contact number.";
                    if (model.id > 0)
                        return RedirectToAction("EditParkerCompany", new { id = model.id });
                    else
                        return RedirectToAction("ParkerCompanyRegistration");
                }
            }

            // ✅ DUPLICATE CHECK: Sub Company Name
            if (!string.IsNullOrWhiteSpace(model.car_parkers_sub_co_name))
            {
                var duplicateSubCoNameSql = model.id > 0
                    ? @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
                WHERE LTRIM(RTRIM(LOWER(car_parkers_sub_co_name))) = LTRIM(RTRIM(LOWER(@subCoName)))
                AND id != @id"
                    : @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
                WHERE LTRIM(RTRIM(LOWER(car_parkers_sub_co_name))) = LTRIM(RTRIM(LOWER(@subCoName)))";

                var subCoNameExists = conn.ExecuteScalar<int>(
                    duplicateSubCoNameSql, new { subCoName = model.car_parkers_sub_co_name, model.id }) > 0;

                if (subCoNameExists)
                {
                    TempData["ErrorMessage"] = $"Sub Company Name '{model.car_parkers_sub_co_name}' already exists! Please use a different sub company name.";
                    if (model.id > 0)
                        return RedirectToAction("EditParkerCompany", new { id = model.id });
                    else
                        return RedirectToAction("ParkerCompanyRegistration");
                }
            }

            // ✅ Get parent company name AND customer ID from master table
            if (!string.IsNullOrEmpty(model.car_parkers_company_id))
            {
                var parentCompany = conn.QueryFirstOrDefault<dynamic>(@"
SELECT 
    car_parkers_company_name,
    customer_id
FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master]
WHERE car_parkers_company_id = @companyId
", new { companyId = model.car_parkers_company_id });

                if (parentCompany != null)
                {
                    if (!string.IsNullOrEmpty(parentCompany.car_parkers_company_name))
                        model.car_parkers_company_name = parentCompany.car_parkers_company_name;

                    // Always use the Customer ID assigned to the selected Parent Company Master
                    if (!string.IsNullOrEmpty(parentCompany.customer_id))
                        model.customer_id = parentCompany.customer_id;
                }
            }

            // ✅ Convert checkbox value to 1 or 0
            int negBalAllowedValue = Request.Form["NegBalAllowed"].ToString() == "on" ? 1 : 0;

            // ✅ Parse card limit (null if empty/invalid)
            string cardLimitValue = string.IsNullOrWhiteSpace(CardLimitInput) ? null : CardLimitInput.Trim();

            if (model.id > 0)
            {
                // ✅ UPDATE
                conn.Execute(@"
            UPDATE [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] SET
                customer_id                      = @customer_id,
                car_parkers_company_id           = @car_parkers_company_id,
                car_parkers_company_name         = @car_parkers_company_name,
                car_parkers_sub_co_id            = @car_parkers_sub_co_id,
                car_parkers_sub_co_name          = @car_parkers_sub_co_name,
                applicable_card_type             = @applicable_card_type,
                sub_co_car_parkers_contact_person= @sub_co_car_parkers_contact_person,
                sub_co_car_parkers_email         = @sub_co_car_parkers_email,
                sub_co_car_parkers_contact_number= @sub_co_car_parkers_contact_number,
                sub_co_car_parkers_address       = @sub_co_car_parkers_address,
                sub_co_car_parkers_pin           = @sub_co_car_parkers_pin,
                sub_co_car_parkers_vat           = @sub_co_car_parkers_vat,
                car_parkers_status               = @car_parkers_status,
                card_parkers_company_password    = @password,
                Neg_Balance_allowed              = @negBalAllowed,
                Total_Card_Limit_Under_Sub_Co    = @cardLimit,
                updated_by                       = @updatedBy,
                updated_on                       = GETDATE()
            WHERE id = @id",
                    new
                    {
                        model.id,
                        model.customer_id,
                        model.car_parkers_company_id,
                        model.car_parkers_company_name,
                        model.car_parkers_sub_co_id,
                        model.car_parkers_sub_co_name,
                        model.applicable_card_type,
                        model.sub_co_car_parkers_contact_person,
                        model.sub_co_car_parkers_email,
                        model.sub_co_car_parkers_contact_number,
                        model.sub_co_car_parkers_address,
                        model.sub_co_car_parkers_pin,
                        model.sub_co_car_parkers_vat,
                        model.car_parkers_status,
                        password = model.card_parkers_company_password,
                        negBalAllowed = negBalAllowedValue,
                        cardLimit = cardLimitValue,
                        updatedBy = currentUser
                    });

                TempData["SuccessMessage"] = "Sub Company updated successfully!";
            }
            else
            {
                // ✅ Get MAX numeric id safely from nvarchar column
                var maxId = conn.ExecuteScalar<int?>(@"
            SELECT MAX(CAST(id AS INT))
            FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
            WHERE ISNUMERIC(id) = 1
              AND id IS NOT NULL
              AND id != ''
        ") ?? 0;

                var newId = maxId + 1;

                // ✅ Safety loop — keep incrementing until we find a free id
                while (conn.ExecuteScalar<int>(@"
            SELECT COUNT(*) 
            FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
            WHERE id = @newId",
                    new { newId = newId.ToString() }) > 0)
                {
                    newId++;
                }

                // ✅ INSERT with safe incremental id
                conn.Execute(@"
            INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
            (id, customer_id, car_parkers_company_id, car_parkers_company_name,
             car_parkers_sub_co_id, car_parkers_sub_co_name, applicable_card_type,
             sub_co_car_parkers_contact_person, sub_co_car_parkers_email,
             sub_co_car_parkers_contact_number, sub_co_car_parkers_address,
             sub_co_car_parkers_pin, sub_co_car_parkers_vat, car_parkers_status,
             card_parkers_company_password, Neg_Balance_allowed, 
             Total_Card_Limit_Under_Sub_Co, Total_Paid_Amount, 
             Total_Deducted_Amount, created_by, created_on)
            VALUES
            (@newId, @customer_id, @car_parkers_company_id, @car_parkers_company_name,
             @car_parkers_sub_co_id, @car_parkers_sub_co_name, @applicable_card_type,
             @sub_co_car_parkers_contact_person, @sub_co_car_parkers_email,
             @sub_co_car_parkers_contact_number, @sub_co_car_parkers_address,
             @sub_co_car_parkers_pin, @sub_co_car_parkers_vat, @car_parkers_status,
             @password, @negBalAllowed, @cardLimit,
             0.00, 0.00, @createdBy, GETDATE())",
                    new
                    {
                        newId = newId.ToString(), // nvarchar column — store as string
                        model.customer_id,
                        model.car_parkers_company_id,
                        model.car_parkers_company_name,
                        model.car_parkers_sub_co_id,
                        model.car_parkers_sub_co_name,
                        model.applicable_card_type,
                        model.sub_co_car_parkers_contact_person,
                        model.sub_co_car_parkers_email,
                        model.sub_co_car_parkers_contact_number,
                        model.sub_co_car_parkers_address,
                        model.sub_co_car_parkers_pin,
                        model.sub_co_car_parkers_vat,
                        model.car_parkers_status,
                        password = model.card_parkers_company_password,
                        negBalAllowed = negBalAllowedValue,
                        cardLimit = cardLimitValue,
                        createdBy = currentUser
                    });

                TempData["SuccessMessage"] = $"Sub Company '{model.car_parkers_sub_co_name}' added successfully! (ID: {newId})";
            }

            return RedirectToAction("ParkerCompanyRegistration");
        }

        [HttpPost]
        public IActionResult DeleteParkerCompany(int id)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());
            conn.Execute("DELETE FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company] WHERE id = @id", new { id });
            TempData["SuccessMessage"] = "Company deleted successfully!";
            return RedirectToAction("ParkerCompanyRegistration");
        }
    }
}
