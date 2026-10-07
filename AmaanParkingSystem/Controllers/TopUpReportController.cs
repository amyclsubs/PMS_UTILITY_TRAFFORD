using AmaanParkingSystem.Controllers;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Text;
using System.Net;
using System.IO;
using System.Text.RegularExpressions;

namespace AmaanParkingSystem.Controllers
{
    public class TopUpReportController : BaseController
    {
        private readonly ILogger<TopUpReportController> _logger;

        public TopUpReportController(ILogger<TopUpReportController> logger)
        {
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Index() => View("~/Views/Reports/TopUpReport.cshtml");

        [HttpGet]
        public async Task<IActionResult> GetTopUpTransactions([FromQuery] TopUpReportRequest request, [FromQuery] List<string> paymentSources)
        {
            try
            {
                request.PaymentSources = paymentSources;

                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                string sql = @"
            SELECT 
                id,
                transaction_id,
                paidamount,
                operator_name,
                transaction_status,
                payment_source,
                car_parkers_sub_co_id,
                car_parkers_sub_co_name,
                created_on,
                created_by,
                Remark,
                mpesa_receipt_number,
                CheckoutRequestID,
                CAST(created_on AS DATE) as payment_date,
                CONVERT(VARCHAR(8), created_on, 108) as payment_time,
                CASE 
                    WHEN CHARINDEX('(', payment_source) > 0 
                    THEN LEFT(payment_source, CHARINDEX('(', payment_source) - 1)
                    ELSE payment_source
                END as payment_source_name,
                CASE 
                    WHEN CHARINDEX('(', payment_source) > 0 
                    THEN REPLACE(REPLACE(SUBSTRING(payment_source, CHARINDEX('(', payment_source) + 1, LEN(payment_source)), ')', ''), '(', '')
                    ELSE ''
                END as payment_mode
            FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
            WHERE transaction_type = 'TOP-UP'
            AND (@StartDate IS NULL OR CAST(created_on AS DATE) >= @StartDate)
            AND (@EndDate IS NULL OR CAST(created_on AS DATE) <= @EndDate)
            AND (@SubCompanyId IS NULL OR car_parkers_sub_co_id = @SubCompanyId)";

                var parameters = new DynamicParameters();
                parameters.Add("StartDate", request.StartDate);
                parameters.Add("EndDate", request.EndDate);
                parameters.Add("SubCompanyId", request.SubCompanyId);

                if (request.PaymentSources != null && request.PaymentSources.Any())
                {
                    var sourceParams = string.Join(", ", request.PaymentSources.Select((s, i) => $"@Source{i}"));
                    sql += $" AND payment_source IN ({sourceParams})";

                    for (int i = 0; i < request.PaymentSources.Count; i++)
                    {
                        parameters.Add($"Source{i}", request.PaymentSources[i]);
                    }
                }

                sql += " ORDER BY created_on DESC";

                var transactions = await conn.QueryAsync<dynamic>(sql, parameters);
                var transactionsList = transactions.ToList();

                var result = new
                {
                    TotalRecords = transactionsList.Count,
                    TotalAmount = transactionsList.Count > 0 ? transactionsList.Sum(t => (decimal)t.paidamount) : 0m,
                    Data = transactionsList
                };

                return Json(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching top-up transactions");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetSubCompanies()
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                string sql = @"
                    SELECT DISTINCT 
                        car_parkers_sub_co_id,
                        car_parkers_sub_co_name
                    FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
                    WHERE car_parkers_sub_co_id IS NOT NULL
                    AND car_parkers_sub_co_name IS NOT NULL
                    AND LTRIM(RTRIM(car_parkers_sub_co_name)) <> ''
                    ORDER BY car_parkers_sub_co_name";

                var companies = await conn.QueryAsync<dynamic>(sql);

                return Json(new { success = true, data = companies });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching sub companies");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetPaymentSources()
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                string sql = @"
            SELECT DISTINCT payment_source
            FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
            WHERE transaction_type = 'TOP-UP'
                AND payment_source IS NOT NULL
                AND LTRIM(RTRIM(payment_source)) <> ''
            ORDER BY payment_source";

                var sources = await conn.QueryAsync<dynamic>(sql);

                return Json(new { success = true, data = sources });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching payment sources");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCompanyDetailsForWaiver(string subCoId)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                string sql = @"
            SELECT 
                car_parkers_sub_co_id,
                car_parkers_sub_co_name,
                sub_co_car_parkers_contact_number,
                applicable_card_type,
                Total_Cards_Under_Sub_Co,
                Total_Card_Limit_Under_Sub_Co,
                sub_co_car_parkers_email,
                Balance_Amount
            FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
            WHERE car_parkers_sub_co_id = @SubCoId";

                var company = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { SubCoId = subCoId });

                if (company == null)
                    return Json(new { success = false, message = "Company not found" });

                return Json(new { success = true, data = company });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching company details for waiver");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> AddWaiverDiscount([FromBody] WaiverDiscountRequest request)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                string transactionId = $"WVR_{DateTime.Now:yyyyMMddHHmmss}_{new Random().Next(1000, 9999)}";
                string paymentSource = request.PaymentMode == "Waiver" ? "Server(Waiver)" : "Server(Discount)";

                string sql = @"
            INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
            (
                transaction_id,
                transaction_type,
                paidamount,
                operator_name,
                transaction_status,
                payment_source,
                status,
                created_by,
                created_on,
                Remark,
                car_parkers_sub_co_id,
                car_parkers_sub_co_name
            )
            VALUES
            (
                @TransactionId,
                'TOP-UP',
                @PaidAmount,
                @OperatorName,
                'Completed',
                @PaymentSource,
                '1',
                @CreatedBy,
                GETDATE(),
                @Remark,
                @SubCompanyId,
                @SubCompanyName
            )";

                await conn.ExecuteAsync(sql, new
                {
                    TransactionId = transactionId,
                    PaidAmount = request.TopUpAmount,
                    OperatorName = request.OperatorName ?? "System",
                    PaymentSource = paymentSource,
                    CreatedBy = request.CreatedBy ?? "Admin",
                    Remark = request.Remark,
                    SubCompanyId = request.SubCompanyId,
                    SubCompanyName = request.SubCompanyName
                });

                return Json(new { success = true, message = "Waiver/Discount added successfully", transactionId = transactionId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding waiver/discount");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCompanyDetailsForTopUp(string subCoId)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                string sql = @"
            SELECT 
                car_parkers_sub_co_id,
                car_parkers_sub_co_name,
                sub_co_car_parkers_contact_number,
                applicable_card_type,
                Total_Cards_Under_Sub_Co,
                Total_Card_Limit_Under_Sub_Co,
                sub_co_car_parkers_email,
                Balance_Amount,
                sub_co_car_parkers_pin
            FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
            WHERE car_parkers_sub_co_id = @SubCoId";

                var company = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { SubCoId = subCoId });

                if (company == null)
                    return Json(new { success = false, message = "Company not found" });

                // Get first card for this company
                string cardSql = @"
            SELECT TOP 1 cardname
            FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated]
            WHERE car_parkers_sub_co_id = @SubCoId
            ORDER BY cardno";

                var firstCard = await conn.QueryFirstOrDefaultAsync<dynamic>(cardSql, new { SubCoId = subCoId });

                return Json(new
                {
                    success = true,
                    data = company,
                    tagNo = firstCard?.cardname ?? ""
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching company details for top-up");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCompanyDetailsByTagNo(string tagNo)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                string cardSql = @"
            SELECT 
                car_parkers_sub_co_id,
                car_parkers_sub_co_name,
                cardno,
                cardname
            FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated]
            WHERE cardname = @TagNo";

                var card = await conn.QueryFirstOrDefaultAsync<dynamic>(cardSql, new { TagNo = tagNo });

                if (card == null)
                    return Json(new { success = false, message = "Tag not found" });

                string companySql = @"
            SELECT 
                car_parkers_sub_co_id,
                car_parkers_sub_co_name,
                sub_co_car_parkers_contact_number,
                applicable_card_type,
                Total_Cards_Under_Sub_Co,
                Total_Card_Limit_Under_Sub_Co,
                sub_co_car_parkers_email,
                Balance_Amount,
                sub_co_car_parkers_pin
            FROM [AMAAN_PMS].[dbo].[db_tbl_08_parkers_company]
            WHERE car_parkers_sub_co_id = @SubCoId";

                var company = await conn.QueryFirstOrDefaultAsync<dynamic>(companySql, new { SubCoId = card.car_parkers_sub_co_id });

                if (company == null)
                    return Json(new { success = false, message = "Company not found" });

                return Json(new
                {
                    success = true,
                    data = company,
                    tagNo = card.cardname,
                    subCoId = card.car_parkers_sub_co_id
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching company details by tag");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCompanyTags(string subCoId)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                string sql = @"
            SELECT 
                cardno,
                cardname,
                car_plate_no,
                balance,
                status,
                card_type
            FROM [AMAAN_PMS].[dbo].[db_tbl_11_cards_allocated]
            WHERE car_parkers_sub_co_id = @SubCoId
            ORDER BY cardno";

                var tags = await conn.QueryAsync<dynamic>(sql, new { SubCoId = subCoId });

                return Json(new { success = true, data = tags });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching company tags");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> AddCompanyTopUp([FromBody] CompanyTopUpRequest request)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                string transactionId = $"TOPUP_{DateTime.Now:yyyyMMddHHmmss}_{new Random().Next(1000, 9999)}";

                // Format payment source
                string paymentSource = request.PaymentMode.ToUpper() switch
                {
                    "MPESA" => "Server(Mpesa)",
                    "CASH" => "Server(Cash)",
                    "MPESA VIA PAYBILL" => "Server(Mpesa Via Paybill)",
                    _ => $"Server({request.PaymentMode})"
                };

                string sql = @"
            INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
            (
                transaction_id,
                transaction_type,
                paidamount,
                operator_name,
                transaction_status,
                payment_source,
                status,
                created_by,
                created_on,
                Remark,
                car_parkers_sub_co_id,
                car_parkers_sub_co_name,
                CheckoutRequestID
            )
            VALUES
            (
                @TransactionId,
                'TOP-UP',
                @PaidAmount,
                @OperatorName,
                'Completed',
                @PaymentSource,
                '1',
                @CreatedBy,
                GETDATE(),
                @Remark,
                @SubCompanyId,
                @SubCompanyName,
                @CheckoutRequestID
            )";

                await conn.ExecuteAsync(sql, new
                {
                    TransactionId = transactionId,
                    PaidAmount = request.TopUpAmount,
                    OperatorName = request.OperatorName ?? "System",
                    PaymentSource = paymentSource,
                    CreatedBy = request.CreatedBy ?? "Admin",
                    Remark = request.Remark,
                    SubCompanyId = request.SubCompanyId,
                    SubCompanyName = request.SubCompanyName,
                    CheckoutRequestID = request.CheckoutRequestID ?? (object)DBNull.Value
                });

                // If CheckoutRequestID exists, start async receipt fetching
                if (!string.IsNullOrEmpty(request.CheckoutRequestID))
                {
                    _ = Task.Run(async () => await FetchAndStoreMpesaReceiptNumber(request.CheckoutRequestID));
                }

                return Json(new { success = true, message = "Company top-up added successfully", transactionId = transactionId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding company top-up");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // ===== M-PESA STK PUSH METHODS =====

        [HttpPost]
        public async Task<IActionResult> InitiateMpesaTopUp([FromBody] MpesaTopUpRequest request)
        {
            try
            {
                var mpesaService = new MpesaTopUpService(GetDynamicConnectionString(), request.PaymentModeName ?? "NEXTGEN R1 MPESA");
                var stkResponse = await mpesaService.InitiateStkPush(request.PhoneNumber, request.Amount, request.TagNumber);

                if (string.IsNullOrEmpty(stkResponse.CheckoutRequestID))
                {
                    return Json(new
                    {
                        success = false,
                        message = stkResponse.ResponseDescription ?? "Failed to initiate STK Push"
                    });
                }

                return Json(new
                {
                    success = true,
                    message = "STK Push sent successfully",
                    checkoutRequestId = stkResponse.CheckoutRequestID,
                    merchantRequestId = stkResponse.MerchantRequestID
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initiating M-Pesa STK Push");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CheckMpesaStatus([FromBody] MpesaStatusRequest request)
        {
            try
            {
                var mpesaService = new MpesaTopUpService(GetDynamicConnectionString(), request.PaymentModeName ?? "NEXTGEN R1 MPESA");
                var statusResponse = await mpesaService.CheckTransactionStatus(request.CheckoutRequestID);

                return Json(new
                {
                    success = true,
                    resultCode = statusResponse.ResultCode,
                    resultDesc = statusResponse.ResultDesc,
                    isComplete = !string.IsNullOrEmpty(statusResponse.ResultCode) && statusResponse.ResultCode != "0",
                    isSuccessful = statusResponse.ResultCode == "0"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking M-Pesa status");
                return Json(new { success = false, message = ex.Message });
            }
        }

        private async Task FetchAndStoreMpesaReceiptNumber(string checkoutRequestId)
        {
            try
            {
                // Poll for receipt number for up to 15 seconds (3 attempts, 5 seconds apart)
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await Task.Delay(5000); // Wait 5 seconds

                    string receiptNumber = await GetMpesaReceiptFromLog(checkoutRequestId);

                    if (!string.IsNullOrEmpty(receiptNumber))
                    {
                        await UpdateMpesaReceiptInDatabase(checkoutRequestId, receiptNumber);
                        _logger.LogInformation($"M-Pesa receipt {receiptNumber} stored for {checkoutRequestId}");
                        return;
                    }
                }

                _logger.LogWarning($"M-Pesa receipt not found for {checkoutRequestId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error fetching M-Pesa receipt for {checkoutRequestId}");
            }
        }

        private async Task<string> GetMpesaReceiptFromLog(string checkoutRequestId)
        {
            try
            {
                string logFileUrl = "https://www.amycl.com/pms-api/mpesa/Nextgen_R1/mpesa_full_log.txt";

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                string logContent = await httpClient.GetStringAsync(logFileUrl);

                if (string.IsNullOrEmpty(logContent))
                    return null;

                string[] logEntries = logContent.Split(
                    new[] { "=============================" },
                    StringSplitOptions.RemoveEmptyEntries
                );

                foreach (string entry in logEntries)
                {
                    if (entry.Contains(checkoutRequestId) &&
                        entry.Contains("CALLBACK") &&
                        entry.Contains("MpesaReceiptNumber"))
                    {
                        var receiptMatch = Regex.Match(
                            entry,
                            "\"Name\"\\s*:\\s*\"MpesaReceiptNumber\"\\s*,\\s*\"Value\"\\s*:\\s*\"([A-Z0-9]+)\"",
                            RegexOptions.IgnoreCase
                        );

                        if (receiptMatch.Success && receiptMatch.Groups.Count > 1)
                        {
                            string receiptNumber = receiptMatch.Groups[1].Value.Trim();

                            if (!string.IsNullOrEmpty(receiptNumber))
                            {
                                _logger.LogInformation($"Receipt found in log: {receiptNumber} for {checkoutRequestId}");
                                return receiptNumber;
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading receipt from log file");
                return null;
            }
        }

        private async Task UpdateMpesaReceiptInDatabase(string checkoutRequestId, string receiptNumber)
        {
            try
            {
                using var conn = new SqlConnection(GetDynamicConnectionString());
                await conn.OpenAsync();

                string query = @"UPDATE [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
                       SET [mpesa_receipt_number] = @ReceiptNumber
                       WHERE [CheckoutRequestID] = @CheckoutRequestID";

                int rowsAffected = await conn.ExecuteAsync(query, new
                {
                    ReceiptNumber = receiptNumber,
                    CheckoutRequestID = checkoutRequestId
                });

                if (rowsAffected > 0)
                {
                    _logger.LogInformation($"M-Pesa receipt {receiptNumber} updated in database");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update M-Pesa receipt");
            }
        }

        // ===== REQUEST/RESPONSE CLASSES =====

        public class CompanyTopUpRequest
        {
            public string SubCompanyId { get; set; }
            public string SubCompanyName { get; set; }
            public decimal TopUpAmount { get; set; }
            public string Remark { get; set; }
            public string PaymentMode { get; set; }
            public string OperatorName { get; set; }
            public string CreatedBy { get; set; }
            public string CheckoutRequestID { get; set; }
        }

        public class MpesaTopUpRequest
        {
            public string TagNumber { get; set; }
            public string PhoneNumber { get; set; }
            public decimal Amount { get; set; }
            public string PaymentModeName { get; set; }
        }

        public class MpesaStatusRequest
        {
            public string CheckoutRequestID { get; set; }
            public string PaymentModeName { get; set; }
        }

        public class WaiverDiscountRequest
        {
            public string SubCompanyId { get; set; }
            public string SubCompanyName { get; set; }
            public decimal TopUpAmount { get; set; }
            public string Remark { get; set; }
            public string PaymentMode { get; set; }
            public string OperatorName { get; set; }
            public string CreatedBy { get; set; }
        }

        public class TopUpReportRequest
        {
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public string SubCompanyId { get; set; }
            public List<string> PaymentSources { get; set; }
        }

        // ===== M-PESA SERVICE CLASS =====

        internal class MpesaTopUpService
        {
            private string _sendRequestUrl;
            private string _statusCheckUrl;
            private string _callbackUrl;
            private string _businessShortCode;
            private string _passKey;
            private string _paymentModeName;
            private string _connStr;

            public MpesaTopUpService(string connectionString, string paymentModeName)
            {
                _connStr = connectionString;
                _paymentModeName = paymentModeName ?? "NEXTGEN R1 MPESA";
                LoadCredentialsFromDatabase();
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            }

            private void LoadCredentialsFromDatabase()
            {
                using var conn = new SqlConnection(_connStr);
                conn.Open();

                string query = @"SELECT [sendRequestUrl], [statusCheckUrl], [callbackUrl], 
                       [businessShortCode], [passKey] 
                       FROM [AMAAN_PMS].[dbo].[db_tbl_20_mpesa_credentials] 
                       WHERE [Payment_Mode_Name] = @PaymentModeName";

                using var cmd = new SqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@PaymentModeName", _paymentModeName);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    _sendRequestUrl = reader["sendRequestUrl"]?.ToString() ?? "";
                    _statusCheckUrl = reader["statusCheckUrl"]?.ToString() ?? "";
                    _callbackUrl = reader["callbackUrl"]?.ToString() ?? "";
                    _businessShortCode = reader["businessShortCode"]?.ToString() ?? "";
                    _passKey = reader["passKey"]?.ToString() ?? "";

                    if (string.IsNullOrEmpty(_sendRequestUrl) || string.IsNullOrEmpty(_statusCheckUrl) ||
                        string.IsNullOrEmpty(_businessShortCode) || string.IsNullOrEmpty(_passKey) ||
                        string.IsNullOrEmpty(_callbackUrl))
                    {
                        throw new Exception($"Incomplete M-Pesa credentials for: {_paymentModeName}");
                    }
                }
                else
                {
                    throw new Exception($"M-Pesa credentials not found for: {_paymentModeName}");
                }
            }

            public async Task<StkPushResponse> InitiateStkPush(string phoneNumber, decimal amount, string accountReference = "TopUp")
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(_sendRequestUrl);
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Accept = "application/json";
                    request.Timeout = 60000;

                    var payload = "{\"BusinessShortCode\":\"" + _businessShortCode +
                                 "\",\"TransactionType\":\"CustomerPayBillOnline\"" +
                                 ",\"Amount\":\"" + Math.Round(amount, 0).ToString() +
                                 "\",\"PartyA\":\"" + phoneNumber +
                                 "\",\"PartyB\":\"" + _businessShortCode +
                                 "\",\"PhoneNumber\":\"" + phoneNumber +
                                 "\",\"AccountReference\":\"" + accountReference +
                                 "\",\"TransactionDesc\":\"Top-up payment for " + accountReference +
                                 "\",\"CallBackURL\":\"" + _callbackUrl +
                                 "\",\"PassKey\":\"" + _passKey + "\"}";

                    var data = Encoding.UTF8.GetBytes(payload);
                    request.ContentLength = data.Length;

                    using (var stream = await request.GetRequestStreamAsync())
                        await stream.WriteAsync(data, 0, data.Length);

                    using var response = (HttpWebResponse)await request.GetResponseAsync();
                    using var streamReader = new StreamReader(response.GetResponseStream());
                    return ParseStkPushResponse(await streamReader.ReadToEndAsync());
                }
                catch (WebException webEx)
                {
                    string errorMessage = "Network error occurred";
                    if (webEx.Response != null)
                        using (var errorStream = webEx.Response.GetResponseStream())
                        using (var errorReader = new StreamReader(errorStream))
                            errorMessage = await errorReader.ReadToEndAsync();
                    throw new Exception("STK Push failed: " + errorMessage);
                }
                catch (Exception ex)
                {
                    throw new Exception("STK Push failed: " + ex.Message);
                }
            }

            public async Task<StkQueryResponse> CheckTransactionStatus(string checkoutRequestId)
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(_statusCheckUrl);
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Accept = "application/json";
                    request.Timeout = 30000;

                    var payload = "{\"BusinessShortCode\":\"" + _businessShortCode +
                                 "\",\"PassKey\":\"" + _passKey +
                                 "\",\"CheckoutRequestID\":\"" + checkoutRequestId + "\"}";

                    var data = Encoding.UTF8.GetBytes(payload);
                    request.ContentLength = data.Length;

                    using (var stream = await request.GetRequestStreamAsync())
                        await stream.WriteAsync(data, 0, data.Length);

                    using var response = (HttpWebResponse)await request.GetResponseAsync();
                    using var streamReader = new StreamReader(response.GetResponseStream());
                    return ParseStkQueryResponse(await streamReader.ReadToEndAsync());
                }
                catch (WebException webEx)
                {
                    string errorMessage = "Network error occurred";
                    if (webEx.Response != null)
                        using (var errorStream = webEx.Response.GetResponseStream())
                        using (var errorReader = new StreamReader(errorStream))
                            errorMessage = await errorReader.ReadToEndAsync();
                    throw new Exception("Status check failed: " + errorMessage);
                }
                catch (Exception ex)
                {
                    throw new Exception("Status check failed: " + ex.Message);
                }
            }

            private StkPushResponse ParseStkPushResponse(string json)
            {
                var response = new StkPushResponse();
                if (json.Contains("stkResponse"))
                {
                    int stkStart = json.IndexOf("\"stkResponse\":");
                    if (stkStart >= 0)
                    {
                        string stkSection = json.Substring(stkStart);
                        if (stkSection.Contains("MerchantRequestID"))
                        {
                            var start = stkSection.IndexOf("\"MerchantRequestID\":\"") + 21;
                            var end = stkSection.IndexOf("\"", start);
                            if (end > start) response.MerchantRequestID = stkSection.Substring(start, end - start);
                        }
                        if (stkSection.Contains("CheckoutRequestID"))
                        {
                            var start = stkSection.IndexOf("\"CheckoutRequestID\":\"") + 21;
                            var end = stkSection.IndexOf("\"", start);
                            if (end > start) response.CheckoutRequestID = stkSection.Substring(start, end - start);
                        }
                        if (stkSection.Contains("ResponseCode"))
                        {
                            var start = stkSection.IndexOf("\"ResponseCode\":\"") + 16;
                            var end = stkSection.IndexOf("\"", start);
                            if (end > start) response.ResponseCode = stkSection.Substring(start, end - start);
                        }
                        if (stkSection.Contains("ResponseDescription"))
                        {
                            var start = stkSection.IndexOf("\"ResponseDescription\":\"") + 23;
                            var end = stkSection.IndexOf("\"", start);
                            if (end > start) response.ResponseDescription = stkSection.Substring(start, end - start);
                        }
                        if (stkSection.Contains("CustomerMessage"))
                        {
                            var start = stkSection.IndexOf("\"CustomerMessage\":\"") + 19;
                            var end = stkSection.IndexOf("\"", start);
                            if (end > start) response.CustomerMessage = stkSection.Substring(start, end - start);
                        }
                    }
                }
                return response;
            }

            private StkQueryResponse ParseStkQueryResponse(string json)
            {
                var response = new StkQueryResponse();
                if (string.IsNullOrEmpty(json))
                {
                    response.ResultCode = "9999";
                    response.ResultDesc = "Empty response";
                    return response;
                }

                try
                {
                    if (json.Contains("stkResponse"))
                    {
                        int stkStart = json.IndexOf("\"stkResponse\":");
                        if (stkStart >= 0)
                            ParseFromSection(json.Substring(stkStart), response);
                    }
                    ParseFromSection(json, response);

                    if (!string.IsNullOrEmpty(response.ResultCode))
                        if (response.ResultCode == "0" || response.ResultCode == "0.0" || response.ResultCode == "00")
                            response.ResultCode = "0";

                    if (string.IsNullOrEmpty(response.ResultCode))
                    {
                        string desc = (response.ResultDesc ?? "").ToLower();
                        string responseDesc = (response.ResponseDescription ?? "").ToLower();
                        string allText = json.ToLower();

                        if (desc.Contains("successful") || responseDesc.Contains("successful") ||
                            desc.Contains("completed") || responseDesc.Contains("completed") ||
                            allText.Contains("successfully processed"))
                        {
                            response.ResultCode = "0";
                            response.ResultDesc = response.ResultDesc ?? "Transaction successful";
                        }
                        else if (allText.Contains("cancel") || allText.Contains("abort") || allText.Contains("declined"))
                        {
                            response.ResultCode = "1032";
                            response.ResultDesc = response.ResultDesc ?? "Request cancelled by user";
                        }
                        else if (allText.Contains("timeout") || allText.Contains("expired"))
                        {
                            response.ResultCode = "1037";
                            response.ResultDesc = response.ResultDesc ?? "Request timeout";
                        }
                        else if (allText.Contains("insufficient") || allText.Contains("balance"))
                        {
                            response.ResultCode = "1001";
                            response.ResultDesc = response.ResultDesc ?? "Insufficient balance";
                        }
                        else if (allText.Contains("error") || allText.Contains("failed"))
                        {
                            response.ResultCode = "9999";
                            response.ResultDesc = response.ResultDesc ?? "Transaction failed";
                        }
                    }
                }
                catch (Exception ex)
                {
                    response.ResultCode = "9998";
                    response.ResultDesc = "Parsing error: " + ex.Message;
                }

                return response;
            }

            private string ExtractJsonValue(string json, string fieldName)
            {
                if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName))
                    return null;

                try
                {
                    string quotedPattern = $"\"{fieldName}\":\"";
                    int quotedStart = json.IndexOf(quotedPattern);
                    if (quotedStart >= 0)
                    {
                        int valueStart = quotedStart + quotedPattern.Length;
                        int valueEnd = json.IndexOf("\"", valueStart);
                        if (valueEnd > valueStart)
                            return json.Substring(valueStart, valueEnd - valueStart).Trim();
                    }

                    string numericPattern = $"\"{fieldName}\":";
                    int numericStart = json.IndexOf(numericPattern);
                    if (numericStart >= 0)
                    {
                        int valueStart = numericStart + numericPattern.Length;
                        while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                            valueStart++;

                        if (valueStart < json.Length)
                        {
                            int valueEnd = valueStart;
                            while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) ||
                                   json[valueEnd] == '.' || json[valueEnd] == '-'))
                                valueEnd++;

                            if (valueEnd > valueStart)
                            {
                                string numericValue = json.Substring(valueStart, valueEnd - valueStart).Trim();
                                if (!string.IsNullOrEmpty(numericValue))
                                    return numericValue;
                            }
                        }
                    }
                }
                catch { }

                return null;
            }

            private void ParseFromSection(string jsonSection, StkQueryResponse response)
            {
                if (string.IsNullOrEmpty(jsonSection))
                    return;

                try
                {
                    if (string.IsNullOrEmpty(response.ResponseCode))
                        response.ResponseCode = ExtractJsonValue(jsonSection, "ResponseCode");
                    if (string.IsNullOrEmpty(response.ResponseDescription))
                        response.ResponseDescription = ExtractJsonValue(jsonSection, "ResponseDescription");
                    if (string.IsNullOrEmpty(response.ResultCode))
                        response.ResultCode = ExtractJsonValue(jsonSection, "ResultCode");
                    if (string.IsNullOrEmpty(response.ResultDesc))
                        response.ResultDesc = ExtractJsonValue(jsonSection, "ResultDesc");
                    if (string.IsNullOrEmpty(response.MerchantRequestID))
                        response.MerchantRequestID = ExtractJsonValue(jsonSection, "MerchantRequestID");
                    if (string.IsNullOrEmpty(response.CheckoutRequestID))
                        response.CheckoutRequestID = ExtractJsonValue(jsonSection, "CheckoutRequestID");
                }
                catch { }
            }
        }

        public class StkPushResponse
        {
            public string MerchantRequestID { get; set; }
            public string CheckoutRequestID { get; set; }
            public string ResponseCode { get; set; }
            public string ResponseDescription { get; set; }
            public string CustomerMessage { get; set; }
        }

        public class StkQueryResponse
        {
            public string ResponseCode { get; set; }
            public string ResponseDescription { get; set; }
            public string MerchantRequestID { get; set; }
            public string CheckoutRequestID { get; set; }
            public string ResultCode { get; set; }
            public string ResultDesc { get; set; }
        }
    }
}