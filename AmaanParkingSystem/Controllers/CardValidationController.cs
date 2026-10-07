using AmaanParkingSystem.Controllers;
using AmaanParkingSystem.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.SqlClient;
using System.Data;

namespace AmaanParkingSystem.Controllers
{
    /// <summary>
    /// Web version of the desktop Card Validation workflow.
    ///
    /// Important:
    /// - This controller is intentionally self-contained so no existing Utility
    ///   controller/service/model files need to be changed.
    /// - It uses the same database tables and validation state values used by
    ///   CARD_VALIDATION/DB_COMM.
    /// - The supplied CARD_VALIDATION source references FEE_SCHEDULE, but the
    ///   FEE_SCHEDULE implementation was not supplied and is not present in
    ///   the PMS_UTILITY project. Therefore this controller does NOT invent a
    ///   fee schedule. It uses the database's current_charges value as the
    ///   chargeable amount and preserves the documented validation state codes.
    /// </summary>
    public class CardValidationController : BaseController
    {
        private readonly IConnectionStringService _connStrService;
        private readonly ILogger<CardValidationController> _logger;

        private const string AuthSessionKey = "UtilityUser";

        private const string CardsTable = "dbo.db_tbl_11_cards_allocated";
        private const string EntryTable = "dbo.db_tbl_14_car_enterd";
        private const string TransactionTable = "dbo.db_tbl_19_transactions";

        // Same state values used by CARD_VALIDATION/DB_COMM:
        // 0 = entered/not validated
        // 1 = validated
        // 10 = revalidation required
        // 11 = revalidated
        private const int StateEntered = 0;
        private const int StateValidated = 1;
        private const int StateRevalidationRequired = 10;
        private const int StateRevalidated = 11;
        private const string ScanTimeSessionPrefix = "CardValidation.ScanTime.";

        public CardValidationController(
            IConnectionStringService connStrService,
            ILogger<CardValidationController> logger)
        {
            _connStrService = connStrService;
            _logger = logger;
        }

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var action = context.ActionDescriptor.RouteValues["action"];

            if (!string.Equals(action, "Index", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(HttpContext.Session.GetString(AuthSessionKey)))
            {
                context.Result = RedirectToAction("Login", "Utility");
                return;
            }

            base.OnActionExecuting(context);
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        /// <summary>
        /// Reads a card from the allocated-card table and, if currently parked,
        /// combines it with the active entry record.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Lookup([FromBody] CardRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.CardNumber))
                return Json(Fail("Please scan/enter a card number."));

            var cardNumber = request.CardNumber.Trim();
            var scanTime = DateTime.Now;
            HttpContext.Session.SetString(ScanTimeSessionPrefix + cardNumber, scanTime.ToString("O"));

            try
            {
                await using var con = new SqlConnection(GetDynamicConnectionString());
                await con.OpenAsync();

                var card = await ReadCardAsync(con, cardNumber);

                if (card == null)
                {
                    return Json(new
                    {
                        success = true,
                        data = new
                        {
                            cardNumber,
                            cardStatus = "NOT OUR CARD",
                            statusClass = "danger",
                            isValidCard = false,
                            scanTime = scanTime.ToString("dd-MMM-yyyy HH:mm:ss")
                        }
                    });
                }

                var entry = await ReadEntryAsync(con, cardNumber);

                if (entry == null)
                {
                    return Json(new
                    {
                        success = true,
                        data = new
                        {
                            cardNumber,
                            cardName = card.CardName,
                            cardType = card.CardType,
                            plateNo = card.CarPlateNo,
                            ownerName = card.CarOwnerName,
                            ownerContact = card.CarOwnerContact,
                            expiryDate = card.ExpiryDate,
                            cardStatus = "NOT ENTERED",
                            statusClass = "warning",
                            isValidCard = true,
                            canValidate = false,
                            needsRevalidation = false,
                            entryTime = "",
                            validationTime = "",
                            revalidationTime = "",
                            parkTime = "00:00:00",
                            currentCharges = 0m,
                            paidAmount = 0m,
                            scanTime = scanTime.ToString("dd-MMM-yyyy HH:mm:ss")
                        }
                    });
                }

                var state = GetValidationState(entry);
                var status = GetStatus(state.IsValidated);

                var now = DateTime.Now;
                var duration = now - entry.EntryTime;
                var parkTime = FormatDuration(duration);

                // current_charges is the amount maintained by the existing
                // parking system/background charge logic.
                var chargeable = entry.CurrentCharges;

                bool needsRevalidation =
                    state.IsValidated == StateRevalidationRequired;

                // If the DB still contains state 1/11 but current_charges is
                // already positive, surface the same business condition to
                // the web operator without inventing a new fee schedule.
                if (!needsRevalidation &&
                    (state.IsValidated == StateValidated ||
                     state.IsValidated == StateRevalidated) &&
                    chargeable > 0.01m)
                {
                    needsRevalidation = true;
                    status = "REVALIDATION REQUIRED";
                }

                return Json(new
                {
                    success = true,
                    data = new
                    {
                        cardNumber,
                        cardName = entry.CardName,
                        cardType = entry.CardType,
                        plateNo = entry.CarPlateNo,
                        ownerName = card.CarOwnerName,
                        ownerContact = card.CarOwnerContact,
                        expiryDate = card.ExpiryDate,

                        cardStatus = status,
                        statusClass = GetStatusClass(status),
                        isValidCard = true,

                        entryTime = entry.EntryTime.ToString("dd-MMM-yyyy HH:mm:ss"),
                        validationTime = entry.ValidationTime?.ToString("dd-MMM-yyyy HH:mm:ss") ?? scanTime.ToString("dd-MMM-yyyy HH:mm:ss"),
                        revalidationTime = entry.RevalidationTime?.ToString("dd-MMM-yyyy HH:mm:ss") ?? "",
                        scanTime = now.ToString("dd-MMM-yyyy HH:mm:ss"),
                        additionalParkTime = entry.ParkMinutesAtValidation > 0
                            ? FormatDuration(TimeSpan.FromMinutes(entry.ParkMinutesAtValidation))
                            : "",

                        parkTime,
                        currentCharges = chargeable,
                        paidAmount = entry.PaidAmount,

                        isValidated = state.IsValidated,
                        canValidate = true,
                        needsRevalidation,
                        canRevalidate = needsRevalidation
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Card lookup failed for {CardNumber}", cardNumber);
                return Json(Fail("System/database error: " + ex.Message));
            }
        }

        /// <summary>
        /// Performs the same DB state transition as DB_COMM.UpdateCardValidationNew:
        /// normal validation -> 1, revalidation -> 11.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Validate([FromBody] ValidateCardRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.CardNumber))
                return Json(Fail("Please scan a card first."));

            var cardNumber = request.CardNumber.Trim();

            try
            {
                await using var con = new SqlConnection(GetDynamicConnectionString());
                await con.OpenAsync();

                // ReadEntryAsync now uses the SAME stored procedure as PayStation.
                var entry = await ReadEntryAsync(con, cardNumber);

                if (entry == null)
                    return Json(Fail("Card not found in parking."));

                var card = await ReadCardAsync(con, cardNumber);

                if (card == null)
                    return Json(Fail("NOT OUR CARD."));

                var state = GetValidationState(entry);

                bool isRevalidation =
                    state.IsValidated == StateRevalidationRequired ||
                    ((state.IsValidated == StateValidated ||
                      state.IsValidated == StateRevalidated) &&
                     entry.CurrentCharges > 0.01m);

                // THIS IS NOW THE SAME CHARGE CALCULATED BY PAYSTATION.
                var chargeableAmount = Math.Max(0m, entry.CurrentCharges);

                var validationTime =
                    GetStoredScanTime(cardNumber) ?? DateTime.Now;

                var parkMinutes = Math.Max(
                    0,
                    (int)(validationTime - entry.EntryTime).TotalMinutes);

                string updateSql = isRevalidation
                    ? $@"
UPDATE {EntryTable}
SET isValidated = @IsValidated,
    revalidation_time = @ValidationTime,
    park_time_untill_validation = @ParkTimeMinutes,
    paid_amount = @PaidAmount,
    current_charges = 0,
    pay_source = 'Server'
WHERE (cardno = @CardNumber OR car_plate_no = @CardNumber)
  AND action_status = 'Entered';"
                    : $@"
UPDATE {EntryTable}
SET isValidated = @IsValidated,
    validation_time = @ValidationTime,
    park_time_untill_validation = @ParkTimeMinutes,
    paid_amount = @PaidAmount,
    current_charges = 0,
    pay_source = 'Server'
WHERE (cardno = @CardNumber OR car_plate_no = @CardNumber)
  AND action_status = 'Entered';";

                await using var cmd = new SqlCommand(updateSql, con);

                cmd.Parameters.Add("@IsValidated", SqlDbType.Int).Value =
                    isRevalidation
                        ? StateRevalidated
                        : StateValidated;

                cmd.Parameters.Add("@ValidationTime", SqlDbType.DateTime)
                    .Value = validationTime;

                cmd.Parameters.Add("@ParkTimeMinutes", SqlDbType.Int)
                    .Value = parkMinutes;

                cmd.Parameters.Add("@PaidAmount", SqlDbType.Decimal)
                    .Value = chargeableAmount;

                cmd.Parameters["@PaidAmount"].Precision = 18;
                cmd.Parameters["@PaidAmount"].Scale = 2;

                cmd.Parameters.Add("@CardNumber", SqlDbType.NVarChar, 255)
                    .Value = cardNumber;

                var affected = await cmd.ExecuteNonQueryAsync();

                if (affected <= 0)
                    return Json(Fail("Failed to update card validation."));

                await InsertValidationTransactionAsync(
                    con,
                    cardNumber,
                    entry,
                    isRevalidation,
                    chargeableAmount,
                    request.PaymentMode ?? "Auto",
                    request.Reason ?? "Server",
                    request.Remark ?? "",
                    request.CheckoutRequestId ?? "");

                HttpContext.Session.Remove(
                    ScanTimeSessionPrefix + cardNumber);

                return Json(new
                {
                    success = true,
                    message = isRevalidation
                        ? $"Card revalidated successfully. Chargeable: {chargeableAmount:F2} KSh."
                        : $"Card validated successfully. Chargeable: {chargeableAmount:F2} KSh.",
                    data = new
                    {
                        cardNumber,
                        isRevalidation,
                        validationTime =
                            validationTime.ToString("dd-MMM-yyyy HH:mm:ss"),
                        chargeableAmount
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Card validation failed for {CardNumber}",
                    cardNumber);

                return Json(Fail("Database error: " + ex.Message));
            }
        }

        /// <summary>
        /// Payment-aware validation. Mirrors UpdateCardValidationWithPaymentDetailsNew:
        /// payment mode/reason/remark are stored on the active entry and the
        /// chargeable amount is stored in paid_amount.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ValidateWithPayment(
    [FromBody] PaymentValidationRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.CardNumber))
                return Json(Fail("Please scan a card first."));

            if (string.IsNullOrWhiteSpace(request.PaymentMode))
                return Json(Fail("Please select a payment mode."));

            if (request.CollectedAmount < 0)
                return Json(Fail("Collected amount cannot be negative."));

            var cardNumber = request.CardNumber.Trim();

            try
            {
                await using var con = new SqlConnection(GetDynamicConnectionString());
                await con.OpenAsync();

                // SAME stored procedure used by PayStation.
                var entry = await ReadEntryAsync(con, cardNumber);

                if (entry == null)
                    return Json(Fail("Card not found in parking."));

                var card = await ReadCardAsync(con, cardNumber);

                if (card == null)
                    return Json(Fail("NOT OUR CARD."));

                var state = GetValidationState(entry);

                bool isRevalidation =
                    state.IsValidated == StateRevalidationRequired ||
                    ((state.IsValidated == StateValidated ||
                      state.IsValidated == StateRevalidated) &&
                     entry.CurrentCharges > 0.01m);

                // SAME CHARGE AS PAYSTATION.
                var chargeableAmount = Math.Max(
                    0m,
                    entry.CurrentCharges);

                var normalizedMode =
                    NormalizePaymentMode(request.PaymentMode);

                // Payment must cover the exact PayStation-calculated charge.
                if (!normalizedMode.Equals(
                        "FOC",
                        StringComparison.OrdinalIgnoreCase) &&
                    request.CollectedAmount < chargeableAmount)
                {
                    return Json(Fail(
                        $"Collected amount must be greater than or equal to KES {chargeableAmount:F2}."));
                }

                if (normalizedMode.Equals(
                        "FOC",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(request.Reason))
                        return Json(Fail("FOC reason is required."));

                    if (CountAlphaNumeric(request.Remark) < 5)
                        return Json(
                            Fail("FOC remark must contain at least 5 characters."));
                }

                if (normalizedMode.Equals(
                        "M-Pesa Via Paybill",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(request.MobileNumber) ||
                        request.MobileNumber.Trim().Length < 10)
                    {
                        return Json(
                            Fail("Enter a valid mobile number (minimum 10 digits)."));
                    }

                    if (string.IsNullOrWhiteSpace(request.TransactionId) ||
                        request.TransactionId.Trim().Length < 6)
                    {
                        return Json(
                            Fail("Enter a valid Transaction ID (minimum 6 characters)."));
                    }
                }

                if (normalizedMode.Equals(
                        "M-Pesa",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(request.MobileNumber) ||
                        request.MobileNumber.Trim().Length < 10)
                    {
                        return Json(
                            Fail("Enter a valid mobile number (minimum 10 digits)."));
                    }
                }

                var validationTime =
                    GetStoredScanTime(cardNumber) ?? DateTime.Now;

                var parkMinutes = Math.Max(
                    0,
                    (int)(validationTime - entry.EntryTime).TotalMinutes);

                string sql = isRevalidation
                    ? $@"
UPDATE {EntryTable}
SET revalidation_time = @ValidationTime,
    park_time_untill_validation = @ParkTimeMinutes,
    isValidated = @IsValidated,
    paid_amount = @PaidAmount,
    current_charges = 0,
    pay_mode = @PaymentMode,
    reason = @Reason,
    remark = @Remark,
    pay_source = 'Server'
WHERE (cardno = @CardNumber OR car_plate_no = @CardNumber)
  AND action_status = 'Entered';"
                    : $@"
UPDATE {EntryTable}
SET validation_time = @ValidationTime,
    park_time_untill_validation = @ParkTimeMinutes,
    isValidated = @IsValidated,
    paid_amount = @PaidAmount,
    current_charges = 0,
    pay_mode = @PaymentMode,
    reason = @Reason,
    remark = @Remark,
    pay_source = 'Server'
WHERE (cardno = @CardNumber OR car_plate_no = @CardNumber)
  AND action_status = 'Entered';";

                await using var cmd = new SqlCommand(sql, con);

                cmd.Parameters.Add("@ValidationTime", SqlDbType.DateTime)
                    .Value = validationTime;

                cmd.Parameters.Add("@ParkTimeMinutes", SqlDbType.Int)
                    .Value = parkMinutes;

                cmd.Parameters.Add("@IsValidated", SqlDbType.Int)
                    .Value = isRevalidation
                        ? StateRevalidated
                        : StateValidated;

                cmd.Parameters.Add("@PaidAmount", SqlDbType.Decimal)
                    .Value = chargeableAmount;

                cmd.Parameters["@PaidAmount"].Precision = 18;
                cmd.Parameters["@PaidAmount"].Scale = 2;

                cmd.Parameters.Add("@PaymentMode", SqlDbType.NVarChar, 255)
                    .Value = normalizedMode;

                cmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 255)
                    .Value = (object?)request.Reason?.Trim()
                             ?? DBNull.Value;

                cmd.Parameters.Add("@Remark", SqlDbType.NVarChar, -1)
                    .Value = (object?)request.Remark?.Trim()
                             ?? DBNull.Value;

                cmd.Parameters.Add("@CardNumber", SqlDbType.NVarChar, 255)
                    .Value = cardNumber;

                var affected = await cmd.ExecuteNonQueryAsync();

                if (affected <= 0)
                    return Json(Fail("Failed to update card validation."));

                var operatorName = request.OperatorName?.Trim();

                if (string.IsNullOrWhiteSpace(operatorName))
                    operatorName = GetLoggedInUser();

                await InsertValidationTransactionAsync(
                    con,
                    cardNumber,
                    entry,
                    isRevalidation,
                    chargeableAmount,
                    normalizedMode,
                    string.IsNullOrWhiteSpace(request.Reason)
                        ? "Server"
                        : request.Reason.Trim(),
                    request.Remark?.Trim() ?? "",
                    request.CheckoutRequestId?.Trim() ?? "",
                    operatorName);

                HttpContext.Session.Remove(
                    ScanTimeSessionPrefix + cardNumber);

                return Json(new
                {
                    success = true,
                    message = isRevalidation
                        ? $"Card revalidated successfully. Chargeable: {chargeableAmount:F2} KSh."
                        : $"Card validated successfully. Chargeable: {chargeableAmount:F2} KSh.",
                    data = new
                    {
                        cardNumber,
                        isRevalidation,
                        paymentMode = normalizedMode,
                        chargeableAmount,
                        collectedAmount = request.CollectedAmount,
                        change = Math.Max(
                            0m,
                            request.CollectedAmount - chargeableAmount),
                        validationTime =
                            validationTime.ToString("dd-MMM-yyyy HH:mm:ss")
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Payment validation failed for {CardNumber}",
                    cardNumber);

                return Json(Fail("Database error: " + ex.Message));
            }
        }

        // ===== PROMPT M-PESA (STK PUSH) - reuses top-up MpesaTopUpService =====
        private const string MpesaPaymentModeName = "TRF_MPESA_PLESK_RS2";

        [HttpPost]
        public async Task<IActionResult> InitiateMpesaPrompt(
    [FromBody] MpesaPromptRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.CardNumber))
                return Json(Fail("Please scan a card first."));

            var phone = (request.PhoneNumber ?? "").Trim();

            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    phone,
                    @"^254[0-9]{9}$"))
            {
                return Json(
                    Fail("Enter a valid mobile number (254XXXXXXXXX)."));
            }

            var cardNumber = request.CardNumber.Trim();

            try
            {
                await using var con = new SqlConnection(
                    GetDynamicConnectionString());

                await con.OpenAsync();

                // SAME PayStation charge calculation.
                var entry = await ReadEntryAsync(con, cardNumber);

                if (entry == null)
                    return Json(Fail("Card not found in parking."));

                var amount = Math.Ceiling(
                    Math.Max(0m, entry.CurrentCharges));

                if (amount < 1)
                    return Json(
                        Fail("No chargeable amount for this card."));

                var mpesa =
                    new TopUpReportController.MpesaTopUpService(
                        GetDynamicConnectionString(),
                        MpesaPaymentModeName);

                var stk = await mpesa.InitiateStkPush(
                    phone,
                    amount,
                    cardNumber);

                if (string.IsNullOrEmpty(stk.CheckoutRequestID))
                {
                    return Json(
                        Fail(stk.ResponseDescription ??
                             "Failed to initiate STK Push."));
                }

                return Json(new
                {
                    success = true,
                    message = "STK Push sent successfully.",
                    checkoutRequestId = stk.CheckoutRequestID,
                    merchantRequestId = stk.MerchantRequestID,
                    amount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "M-Pesa STK Push failed for {CardNumber}",
                    cardNumber);

                return Json(Fail(ex.Message));
            }
        }

        [HttpPost]
        public async Task<IActionResult> CheckMpesaPrompt([FromBody] MpesaPromptStatusRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.CheckoutRequestId))
                return Json(Fail("Missing CheckoutRequestID."));

            try
            {
                var mpesa = new TopUpReportController.MpesaTopUpService(
                    GetDynamicConnectionString(), MpesaPaymentModeName);

                var status = await mpesa.CheckTransactionStatus(request.CheckoutRequestId.Trim());

                return Json(new
                {
                    success = true,
                    resultCode = status.ResultCode,
                    resultDesc = status.ResultDesc
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "M-Pesa status check failed");
                return Json(Fail(ex.Message));
            }
        }

        [HttpGet]
        public async Task<IActionResult> FocReasons()
        {
            try
            {
                await using var con = new SqlConnection(GetDynamicConnectionString());
                await con.OpenAsync();

                // Keep this deliberately tolerant because the desktop source
                // loads reasons through its existing DB helper.
                var values = new List<string>();
                const string sql = @"
SELECT Reason
FROM [AMAAN_PMS].[dbo].[db_tbl_25_FOC_Reason]
WHERE Reason IS NOT NULL
ORDER BY Reason;";

                await using var cmd = new SqlCommand(sql, con);
                await using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var value = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
                    if (!string.IsNullOrWhiteSpace(value) &&
                        !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        values.Add(value);
                    }
                }

                return Json(new { success = true, data = values });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load FOC reasons");
                return Json(Fail("Failed to load FOC reasons: " + ex.Message));
            }
        }

        private async Task<CardRow?> ReadCardAsync(
    SqlConnection con,
    string cardNumber)
        {
            const string allocatedSql = $@"
SELECT TOP 1
       cardno,
       cardname,
       card_type,
       car_plate_no,
       car_owner_name,
       car_owner_contact_no,
       expiry_date,
       status,
       balance,
       customer_id,
       car_parkers_company_id,
       ISNULL(access_rights, '111') AS access_rights
FROM {CardsTable}
WHERE cardno = @CardNumber;";

            await using (var cmd = new SqlCommand(allocatedSql, con))
            {
                cmd.Parameters.Add(
                    "@CardNumber",
                    SqlDbType.NVarChar,
                    255).Value = cardNumber;

                await using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    return new CardRow
                    {
                        CardNumber = Value(reader, "cardno"),
                        CardName = Value(reader, "cardname"),
                        CardType = Value(reader, "card_type"),
                        CarPlateNo = Value(reader, "car_plate_no"),
                        CarOwnerName = Value(reader, "car_owner_name"),
                        CarOwnerContact = Value(reader, "car_owner_contact_no"),
                        ExpiryDate = NullableDate(reader, "expiry_date"),
                        IsActive = ToBool(reader["status"]),
                        Balance = ToDecimal(reader["balance"]),
                        CustomerId = Value(reader, "customer_id"),
                        CompanyId = Value(reader, "car_parkers_company_id"),
                        AccessRights = Value(reader, "access_rights")
                    };
                }
            }

            // If the card is not allocated, use the active parking entry.
            // Search by either card number OR vehicle plate number.
            const string entrySql = $@"
SELECT TOP 1
       cardno,
       card_name,
       card_type,
       car_plate_no
FROM {EntryTable}
WHERE (cardno = @CardNumber OR car_plate_no = @CardNumber)
  AND action_status = 'Entered'
ORDER BY entry_time DESC;";

            await using (var cmd = new SqlCommand(entrySql, con))
            {
                cmd.Parameters.Add(
                    "@CardNumber",
                    SqlDbType.NVarChar,
                    255).Value = cardNumber;

                await using var reader = await cmd.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                    return null;

                return new CardRow
                {
                    CardNumber = Value(reader, "cardno"),
                    CardName = Value(reader, "card_name"),
                    CardType = Value(reader, "card_type"),
                    CarPlateNo = Value(reader, "car_plate_no"),
                    CarOwnerName = "",
                    CarOwnerContact = "",
                    ExpiryDate = null,
                    IsActive = true,
                    Balance = 0m,
                    CustomerId = "",
                    CompanyId = "",
                    AccessRights = "111"
                };
            }
        }
        private async Task<EntryRow?> ReadEntryAsync(SqlConnection con, string cardNumber)
        {
            decimal calculatedCharges = 0m;

            // ============================================================
            // STEP 1
            // Use EXACTLY the same stored procedure as PayStation.
            // ============================================================
            await using (var chargeCmd = new SqlCommand(
                "dbo.sp_GetCardDetailsWithCharges",
                con))
            {
                chargeCmd.CommandType = CommandType.StoredProcedure;

                chargeCmd.Parameters.Add("@search", SqlDbType.NVarChar, 100)
                    .Value = cardNumber;

                chargeCmd.Parameters.Add("@match_plate", SqlDbType.Bit)
                    .Value = true;

                await using var chargeReader =
                    await chargeCmd.ExecuteReaderAsync();

                if (!await chargeReader.ReadAsync())
                    return null;

                // This is the SAME current_charges returned to PayStation.
                if (chargeReader["current_charges"] != DBNull.Value)
                {
                    decimal.TryParse(
                        chargeReader["current_charges"]?.ToString(),
                        out calculatedCharges);
                }
            }

            // ============================================================
            // STEP 2
            // Read the remaining fields that the stored procedure does
            // not return, especially card_type.
            // ============================================================
            const string entrySql = $@"
SELECT TOP 1
       cardno,
       card_name,
       card_type,
       entry_time,
       car_plate_no,
       validation_time,
       revalidation_time,
       ISNULL(isValidated, 0) AS isValidated,
       ISNULL(paid_amount, 0) AS paid_amount,
       ISNULL(park_time_untill_validation, 0) AS park_time_untill_validation
FROM {EntryTable}
WHERE (cardno = @CardNumber
       OR car_plate_no = @CardNumber)
  AND action_status = 'Entered'
ORDER BY entry_time DESC;";

            await using var entryCmd =
                new SqlCommand(entrySql, con);

            entryCmd.Parameters.Add("@CardNumber", SqlDbType.NVarChar, 255)
                .Value = cardNumber;

            await using var reader =
                await entryCmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
                return null;

            return new EntryRow
            {
                CardNumber = Value(reader, "cardno"),

                CardName = Value(reader, "card_name"),

                // Read from entry table because the stored procedure
                // does not return card_type.
                CardType = Value(reader, "card_type"),

                EntryTime = Convert.ToDateTime(reader["entry_time"]),

                CarPlateNo = Value(reader, "car_plate_no"),

                ValidationTime =
                    NullableDate(reader, "validation_time"),

                RevalidationTime =
                    NullableDate(reader, "revalidation_time"),

                IsValidated =
                    ToInt(reader["isValidated"]),

                // IMPORTANT:
                // Do NOT use db_tbl_14_car_enterd.current_charges here.
                // Use the charge calculated by the PayStation procedure.
                CurrentCharges = calculatedCharges,

                PaidAmount =
                    ToDecimal(reader["paid_amount"]),

                ParkMinutesAtValidation =
                    ToInt(reader["park_time_untill_validation"])
            };
        }
        private static bool HasColumn(SqlDataReader reader, string columnName)
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (string.Equals(
                    reader.GetName(i),
                    columnName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        private async Task InsertValidationTransactionAsync(
            SqlConnection con,
            string cardNumber,
            EntryRow entry,
            bool isRevalidation,
            decimal chargeableAmount,
            string paymentMode,
            string reason,
            string remark,
            string checkoutRequestId,
            string? operatorName = null)
        {
            // Same transaction table and columns used by DB_COMM.LogValidationTransaction.
            const string sql = $@"
INSERT INTO {TransactionTable}
(
    transaction_id,
    car_plate_no,
    cardno,
    cardname,
    transaction_type,
    paidamount,
    operator_name,
    transaction_status,
    payment_source,
    status,
    created_by,
    created_on,
    entry_time,
    validation_time,
    validation_charges,
    validation_paymode,
    revalidation_time,
    revalidation_charges,
    revalidation_paymode,
    CheckoutRequestID
)
VALUES
(
    @TransactionId,
    @CarPlateNo,
    @CardNo,
    @CardName,
    @TransactionType,
    @PaidAmount,
    @OperatorName,
    'Completed',
    'Server',
    1,
    @CreatedBy,
    GETDATE(),
    @EntryTime,
    @ValidationTime,
    @ValidationCharges,
    @ValidationPayMode,
    @RevalidationTime,
    @RevalidationCharges,
    @RevalidationPayMode,
    @CheckoutRequestID
);";

            await using var cmd = new SqlCommand(sql, con);

            var now = DateTime.Now;
            var user = string.IsNullOrWhiteSpace(operatorName) ? "Server" : operatorName;

            cmd.Parameters.Add("@TransactionId", SqlDbType.NVarChar, 100).Value =
                "VAL-" + now.ToString("yyyyMMddHHmmssfff") + "-" +
                Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

            cmd.Parameters.Add("@CarPlateNo", SqlDbType.NVarChar, 100).Value =
                (object?)entry.CarPlateNo ?? DBNull.Value;
            cmd.Parameters.Add("@CardNo", SqlDbType.NVarChar, 255).Value = cardNumber;
            cmd.Parameters.Add("@CardName", SqlDbType.NVarChar, 255).Value =
                (object?)entry.CardName ?? DBNull.Value;
            cmd.Parameters.Add("@TransactionType", SqlDbType.NVarChar, 50).Value =
                isRevalidation ? "Revalidation" : "Validation";
            cmd.Parameters.Add("@PaidAmount", SqlDbType.Decimal).Value = chargeableAmount;
            cmd.Parameters["@PaidAmount"].Precision = 18;
            cmd.Parameters["@PaidAmount"].Scale = 2;
            cmd.Parameters.Add("@OperatorName", SqlDbType.NVarChar, 255).Value = user;
            cmd.Parameters.Add("@CreatedBy", SqlDbType.NVarChar, 255).Value = user;
            cmd.Parameters.Add("@EntryTime", SqlDbType.DateTime).Value = entry.EntryTime;

            if (isRevalidation)
            {
                cmd.Parameters.Add("@ValidationTime", SqlDbType.DateTime).Value = DBNull.Value;
                cmd.Parameters.Add("@ValidationCharges", SqlDbType.Decimal).Value = DBNull.Value;
                cmd.Parameters["@ValidationCharges"].Precision = 18;
                cmd.Parameters["@ValidationCharges"].Scale = 2;
                cmd.Parameters.Add("@ValidationPayMode", SqlDbType.NVarChar, 100).Value = DBNull.Value;

                cmd.Parameters.Add("@RevalidationTime", SqlDbType.DateTime).Value =
                    entry.RevalidationTime.HasValue
                        ? entry.RevalidationTime.Value
                        : now;

                cmd.Parameters.Add("@RevalidationCharges", SqlDbType.Decimal).Value = chargeableAmount;
                cmd.Parameters["@RevalidationCharges"].Precision = 18;
                cmd.Parameters["@RevalidationCharges"].Scale = 2;
                cmd.Parameters.Add("@RevalidationPayMode", SqlDbType.NVarChar, 100).Value =
                    string.IsNullOrWhiteSpace(paymentMode) ? "Auto" : paymentMode;
            }
            else
            {
                cmd.Parameters.Add("@ValidationTime", SqlDbType.DateTime).Value =
                    entry.ValidationTime.HasValue ? entry.ValidationTime.Value : now;

                cmd.Parameters.Add("@ValidationCharges", SqlDbType.Decimal).Value = chargeableAmount;
                cmd.Parameters["@ValidationCharges"].Precision = 18;
                cmd.Parameters["@ValidationCharges"].Scale = 2;
                cmd.Parameters.Add("@ValidationPayMode", SqlDbType.NVarChar, 100).Value =
                    string.IsNullOrWhiteSpace(paymentMode) ? "Auto" : paymentMode;

                cmd.Parameters.Add("@RevalidationTime", SqlDbType.DateTime).Value = DBNull.Value;
                cmd.Parameters.Add("@RevalidationCharges", SqlDbType.Decimal).Value = DBNull.Value;
                cmd.Parameters["@RevalidationCharges"].Precision = 18;
                cmd.Parameters["@RevalidationCharges"].Scale = 2;
                cmd.Parameters.Add("@RevalidationPayMode", SqlDbType.NVarChar, 100).Value = DBNull.Value;
            }

            cmd.Parameters.Add("@CheckoutRequestID", SqlDbType.NVarChar, 255).Value =
                string.IsNullOrWhiteSpace(checkoutRequestId)
                    ? DBNull.Value
                    : checkoutRequestId;

            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                // The desktop DB_COMM treats transaction-log failure as non-fatal
                // to the validation itself. Preserve that behaviour.
                _logger.LogWarning(ex,
                    "Validation succeeded but transaction log failed for {CardNumber}",
                    cardNumber);
            }
        }

        private DateTime? GetStoredScanTime(string cardNumber)
        {
            var raw = HttpContext.Session.GetString(ScanTimeSessionPrefix + cardNumber);
            return DateTime.TryParse(raw, out var value) ? value : null;
        }

        private static ValidationState GetValidationState(EntryRow entry)
        {
            return new ValidationState
            {
                IsValidated = entry.IsValidated,
                ValidationTime = entry.ValidationTime,
                RevalidationTime = entry.RevalidationTime
            };
        }

        private static string GetStatus(int state)
        {
            return state switch
            {
                StateValidated => "VALIDATED",
                StateRevalidated => "REVALIDATED",
                StateRevalidationRequired => "REVALIDATION REQUIRED",
                _ => "ENTERED"
            };
        }

        private static string GetStatusClass(string status)
        {
            return status switch
            {
                "VALIDATED" => "primary",
                "REVALIDATED" => "purple",
                "REVALIDATION REQUIRED" => "warning",
                "NOT ENTERED" => "warning",
                "ENTERED" => "success",
                _ => "danger"
            };
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalSeconds < 0)
                value = TimeSpan.Zero;

            return $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";
        }

        private static string NormalizePaymentMode(string mode)
        {
            var m = (mode ?? "").Trim();

            if (m.Equals("Cash", StringComparison.OrdinalIgnoreCase))
                return "Cash";
            if (m.Equals("Manual M-Pesa", StringComparison.OrdinalIgnoreCase))
                return "M-Pesa Via Paybill";
            if (m.Equals("M-Pesa Via Paybill", StringComparison.OrdinalIgnoreCase))
                return "M-Pesa Via Paybill";
            if (m.Equals("Prompt M-Pesa", StringComparison.OrdinalIgnoreCase))
                return "M-Pesa";
            if (m.Equals("M-Pesa", StringComparison.OrdinalIgnoreCase))
                return "M-Pesa";
            if (m.Equals("FOC", StringComparison.OrdinalIgnoreCase))
                return "FOC";

            return m;
        }

        private static int CountAlphaNumeric(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            return value.Count(char.IsLetterOrDigit);
        }

        private static string Value(SqlDataReader reader, string name)
            => reader[name] == DBNull.Value ? "" : reader[name]?.ToString() ?? "";

        private static decimal ToDecimal(object value)
            => value == DBNull.Value || value == null ? 0m : Convert.ToDecimal(value);

        private static int ToInt(object value)
            => value == DBNull.Value || value == null ? 0 : Convert.ToInt32(value);

        private static bool ToBool(object value)
        {
            if (value == DBNull.Value || value == null)
                return false;

            if (value is bool b)
                return b;

            if (value is int i)
                return i == 1;

            var s = value.ToString()?.Trim().ToUpperInvariant();
            return s is "1" or "TRUE" or "ACTIVE";
        }

        private static DateTime? NullableDate(SqlDataReader reader, string name)
        {
            return reader[name] == DBNull.Value
                ? null
                : Convert.ToDateTime(reader[name]);
        }

        private static object Fail(string message)
            => new { success = false, message };

        public sealed class MpesaPromptRequest
        {
            public string? CardNumber { get; set; }
            public string? PhoneNumber { get; set; }
        }

        public sealed class MpesaPromptStatusRequest
        {
            public string? CheckoutRequestId { get; set; }
        }

        public sealed class CardRequest
        {
            public string? CardNumber { get; set; }
        }

        public sealed class ValidateCardRequest
        {
            public string? CardNumber { get; set; }
            public string? PaymentMode { get; set; }
            public string? Reason { get; set; }
            public string? Remark { get; set; }
            public string? CheckoutRequestId { get; set; }
        }

        public sealed class PaymentValidationRequest
        {
            public string? CardNumber { get; set; }
            public string? PaymentMode { get; set; }
            public string? Reason { get; set; }
            public string? Remark { get; set; }
            public string? MobileNumber { get; set; }
            public string? TransactionId { get; set; }
            public string? OperatorName { get; set; }
            public string? CheckoutRequestId { get; set; }
            public decimal CollectedAmount { get; set; }
        }

        private sealed class CardRow
        {
            public string CardNumber { get; set; } = "";
            public string CardName { get; set; } = "";
            public string CardType { get; set; } = "";
            public string CarPlateNo { get; set; } = "";
            public string CarOwnerName { get; set; } = "";
            public string CarOwnerContact { get; set; } = "";
            public DateTime? ExpiryDate { get; set; }
            public bool IsActive { get; set; }
            public decimal Balance { get; set; }
            public string CustomerId { get; set; } = "";
            public string CompanyId { get; set; } = "";
            public string AccessRights { get; set; } = "";
        }

        private sealed class EntryRow
        {
            public string CardNumber { get; set; } = "";
            public string CardName { get; set; } = "";
            public string CardType { get; set; } = "";
            public DateTime EntryTime { get; set; }
            public string CarPlateNo { get; set; } = "";
            public DateTime? ValidationTime { get; set; }
            public DateTime? RevalidationTime { get; set; }
            public int IsValidated { get; set; }
            public decimal CurrentCharges { get; set; }
            public decimal PaidAmount { get; set; }
            public int ParkMinutesAtValidation { get; set; }
        }

        private sealed class ValidationState
        {
            public int IsValidated { get; set; }
            public DateTime? ValidationTime { get; set; }
            public DateTime? RevalidationTime { get; set; }
        }
    }
}
