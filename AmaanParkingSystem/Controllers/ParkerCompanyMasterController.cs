using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using Dapper;
using AmaanParkingSystem.Models.UserManagement;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace AmaanParkingSystem.Controllers
{
    public class ParkerCompanyMasterController : BaseController
    {
        public ParkerCompanyMasterController()
        {
        }

        // Generate next Company ID (CPC_0001, CPC_0002, etc.)
        private string GenerateNextCompanyId()
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            // ✅ FIX: Get the MAX numeric value from CPC_XXXX, not ORDER BY id DESC
            var lastId = conn.QuerySingleOrDefault<string>(@"
        SELECT TOP 1 car_parkers_company_id 
        FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master] 
        WHERE car_parkers_company_id LIKE 'CPC_%'
          AND ISNUMERIC(REPLACE(car_parkers_company_id, 'CPC_', '')) = 1
        ORDER BY CAST(REPLACE(car_parkers_company_id, 'CPC_', '') AS INT) DESC
    ");

            if (string.IsNullOrEmpty(lastId))
            {
                return "CPC_0001";
            }

            var numberPart = lastId.Replace("CPC_", "");
            if (int.TryParse(numberPart, out int currentNumber))
            {
                int nextNumber = currentNumber + 1;
                return $"CPC_{nextNumber:D4}";
            }

            return "CPC_0001";
        }

        [HttpGet]
        public IActionResult ParkerCompanyMaster(string sortColumn = null, string sortOrder = "asc")
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            var sites = conn.Query("SELECT customer_id, site_name FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]").ToList();
            ViewBag.Sites = new SelectList(sites, "customer_id", "site_name");

            // Build dynamic ORDER BY clause
            string orderByClause = "ORDER BY id ASC";

            if (!string.IsNullOrEmpty(sortColumn))
            {
                switch (sortColumn.ToLower())
                {
                    case "companyname":
                        orderByClause = $"ORDER BY car_parkers_company_name {sortOrder.ToUpper()}";
                        break;
                    default:
                        orderByClause = "ORDER BY id ASC";
                        break;
                }
            }

            var companyList = conn.Query<ParkerCompanyMasterModel>($@"
        SELECT * FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master] {orderByClause}
    ").ToList();

            ViewBag.CompanyList = companyList;
            ViewBag.SortColumn = sortColumn;
            ViewBag.SortOrder = sortOrder;

            // Generate next Company ID for new entry
            var newModel = new ParkerCompanyMasterModel
            {
                car_parkers_company_id = GenerateNextCompanyId()
            };

            return View("~/Views/UserManagement/ParkerCompanyMaster.cshtml", newModel);
        }

        [HttpGet]
        public IActionResult EditParkerCompanyMaster(int id, string sortColumn = null, string sortOrder = "asc")
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());

            var sites = conn.Query("SELECT customer_id, site_name FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]").ToList();
            ViewBag.Sites = new SelectList(sites, "customer_id", "site_name");

            var company = conn.QuerySingleOrDefault<ParkerCompanyMasterModel>(@"
        SELECT * FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master] WHERE id=@id
    ", new { id });

            if (company == null)
                return RedirectToAction("ParkerCompanyMaster");

            // Build dynamic ORDER BY clause
            string orderByClause = "ORDER BY id ASC";

            if (!string.IsNullOrEmpty(sortColumn))
            {
                switch (sortColumn.ToLower())
                {
                    case "companyname":
                        orderByClause = $"ORDER BY car_parkers_company_name {sortOrder.ToUpper()}";
                        break;
                    default:
                        orderByClause = "ORDER BY id ASC";
                        break;
                }
            }

            var companyList = conn.Query<ParkerCompanyMasterModel>($@"
        SELECT * FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master] {orderByClause}
    ").ToList();

            ViewBag.CompanyList = companyList;
            ViewBag.SortColumn = sortColumn;
            ViewBag.SortOrder = sortOrder;
            ViewBag.IsEdit = true;

            return View("~/Views/UserManagement/ParkerCompanyMaster.cshtml", company);
        }

        [HttpGet]
        public IActionResult ExportParkerCompaniesToExcel(string sortColumn = null, string sortOrder = "asc")
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
                    default:
                        orderByClause = "ORDER BY id ASC";
                        break;
                }
            }

            var companyList = conn.Query<ParkerCompanyMasterModel>($@"
        SELECT * FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master] {orderByClause}
    ").ToList();

            // Find duplicate Email and Contact Numbers
            var duplicateEmails = companyList
                .Where(c => !string.IsNullOrEmpty(c.car_parkers_email))
                .GroupBy(c => c.car_parkers_email)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            var duplicateContacts = companyList
                .Where(c => !string.IsNullOrEmpty(c.car_parkers_contact_number))
                .GroupBy(c => c.car_parkers_contact_number)
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
        "ID", "Customer ID", "Company ID", "Company Name", "Contact Person",
        "Email", "Contact Number", "Address", "Status",
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
            foreach (var company in companyList)
            {
                var row = sheet.CreateRow(rowNum);

                row.CreateCell(0).SetCellValue(company.id);

                var cell1 = row.CreateCell(1);
                if (!string.IsNullOrEmpty(company.customer_id)) cell1.SetCellValue(company.customer_id);

                var cell2 = row.CreateCell(2);
                if (!string.IsNullOrEmpty(company.car_parkers_company_id)) cell2.SetCellValue(company.car_parkers_company_id);

                var cell3 = row.CreateCell(3);
                if (!string.IsNullOrEmpty(company.car_parkers_company_name)) cell3.SetCellValue(company.car_parkers_company_name);

                var cell4 = row.CreateCell(4);
                if (!string.IsNullOrEmpty(company.car_parkers_contact_person)) cell4.SetCellValue(company.car_parkers_contact_person);

                // Email - with duplicate highlighting
                var emailCell = row.CreateCell(5);
                if (!string.IsNullOrEmpty(company.car_parkers_email))
                {
                    emailCell.SetCellValue(company.car_parkers_email);
                    if (duplicateEmails.Contains(company.car_parkers_email))
                    {
                        emailCell.CellStyle = duplicateStyle;
                    }
                }

                // Contact Number - with duplicate highlighting
                var contactCell = row.CreateCell(6);
                if (!string.IsNullOrEmpty(company.car_parkers_contact_number))
                {
                    contactCell.SetCellValue(company.car_parkers_contact_number);
                    if (duplicateContacts.Contains(company.car_parkers_contact_number))
                    {
                        contactCell.CellStyle = duplicateStyle;
                    }
                }

                var cell7 = row.CreateCell(7);
                if (!string.IsNullOrEmpty(company.car_parkers_address)) cell7.SetCellValue(company.car_parkers_address);

                var cell8 = row.CreateCell(8);
                cell8.SetCellValue(company.car_parkers_status == "1" ? "Active" : "Inactive");

                var cell9 = row.CreateCell(9);
                if (!string.IsNullOrEmpty(company.created_by)) cell9.SetCellValue(company.created_by);

                var cell10 = row.CreateCell(10);
                if (company.created_on.HasValue)
                    cell10.SetCellValue(company.created_on.Value.ToString("dd-MMM-yyyy HH:mm:ss").ToUpper());

                var cell11 = row.CreateCell(11);
                if (!string.IsNullOrEmpty(company.updated_by)) cell11.SetCellValue(company.updated_by);

                var cell12 = row.CreateCell(12);
                if (company.updated_on.HasValue)
                    cell12.SetCellValue(company.updated_on.Value.ToString("dd-MMM-yyyy HH:mm:ss").ToUpper());

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

        [HttpPost]
        public IActionResult AddOrEditParkerCompanyMaster(ParkerCompanyMasterModel model)
        {
            using var conn = new SqlConnection(GetDynamicConnectionString());
            var currentUser = GetLoggedInUser();

            // ✅ DUPLICATE CHECK: Company Name
            var duplicateCompanyCheckSql = model.id > 0
                ? @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master]
            WHERE LTRIM(RTRIM(LOWER(car_parkers_company_name))) = LTRIM(RTRIM(LOWER(@name)))
            AND CAST(id AS NVARCHAR(MAX)) != CAST(@id AS NVARCHAR(MAX))"
                : @"SELECT COUNT(*) FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master]
            WHERE LTRIM(RTRIM(LOWER(car_parkers_company_name))) = LTRIM(RTRIM(LOWER(@name)))";

            var companyExists = conn.ExecuteScalar<int>(
                duplicateCompanyCheckSql, new { name = model.car_parkers_company_name, model.id }) > 0;

            if (companyExists)
            {
                TempData["ErrorMessage"] = $"Company Name '{model.car_parkers_company_name}' already exists! Please use a different company name.";
                if (model.id > 0)
                    return RedirectToAction("EditParkerCompanyMaster", new { id = model.id });
                else
                    return RedirectToAction("ParkerCompanyMaster");
            }

            if (model.id == 0)
            {
                // ✅ Get MAX numeric id — cast nvarchar to int safely
                var maxId = conn.ExecuteScalar<int?>(@"
            SELECT MAX(CAST(id AS INT))
            FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master]
            WHERE ISNUMERIC(id) = 1
              AND id IS NOT NULL
              AND id != ''
        ") ?? 0;

                var newId = maxId + 1;

                // ✅ Safety loop — keep incrementing until we find a free id
                while (conn.ExecuteScalar<int>(@"
            SELECT COUNT(*) 
            FROM [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master]
            WHERE id = @newId",
                    new { newId = newId.ToString() }) > 0)
                {
                    newId++;
                }

                // ✅ Auto-generate Company ID
                model.car_parkers_company_id = GenerateNextCompanyId();

                // ✅ INSERT — store id as string since column is nvarchar
                conn.Execute(@"
            INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master]
            (id, customer_id, car_parkers_company_id, car_parkers_company_name,
             car_parkers_contact_person, car_parkers_email, car_parkers_contact_number,
             car_parkers_address, car_parkers_status, created_by, created_on)
            VALUES
            (@newId, @customer_id, @car_parkers_company_id, @car_parkers_company_name,
             @car_parkers_contact_person, @car_parkers_email, @car_parkers_contact_number,
             @car_parkers_address, @car_parkers_status, @created_by, GETDATE())", new
                {
                    newId = newId.ToString(),
                    model.customer_id,
                    model.car_parkers_company_id,
                    model.car_parkers_company_name,
                    model.car_parkers_contact_person,
                    model.car_parkers_email,
                    model.car_parkers_contact_number,
                    model.car_parkers_address,
                    model.car_parkers_status,
                    created_by = currentUser
                });

                TempData["SuccessMessage"] = $"Company '{model.car_parkers_company_name}' added successfully! (ID: {newId} | {model.car_parkers_company_id})";
            }
            else
            {
                // ✅ UPDATE — id is nvarchar so cast for WHERE clause
                conn.Execute(@"
            UPDATE [AMAAN_PMS].[dbo].[db_tbl_08A_parkers_company_master] SET
                customer_id                = @customer_id,
                car_parkers_company_name   = @car_parkers_company_name,
                car_parkers_contact_person = @car_parkers_contact_person,
                car_parkers_email          = @car_parkers_email,
                car_parkers_contact_number = @car_parkers_contact_number,
                car_parkers_address        = @car_parkers_address,
                car_parkers_status         = @car_parkers_status,
                updated_by                 = @updated_by,
                updated_on                 = GETDATE()
            WHERE CAST(id AS NVARCHAR(MAX)) = CAST(@id AS NVARCHAR(MAX))", new
                {
                    model.id,
                    model.customer_id,
                    model.car_parkers_company_name,
                    model.car_parkers_contact_person,
                    model.car_parkers_email,
                    model.car_parkers_contact_number,
                    model.car_parkers_address,
                    model.car_parkers_status,
                    updated_by = currentUser
                });

                TempData["SuccessMessage"] = "Company updated successfully!";
            }

            return RedirectToAction("ParkerCompanyMaster");
        }
    }
}
