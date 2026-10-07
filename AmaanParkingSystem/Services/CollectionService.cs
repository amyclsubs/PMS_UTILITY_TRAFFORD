using Microsoft.Data.SqlClient;
using Dapper;
using AmaanParkingSystem.Models.Collection;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Services
{
    public class CollectionService
    {
        private readonly IConnectionStringService _connStrService;

        public CollectionService(IConnectionStringService connStrService)
        {
            _connStrService = connStrService;
        }

        private SqlConnection GetConnection()
        {
            var cs = _connStrService.GetConnectionString();
            if (string.IsNullOrEmpty(cs))
                throw new InvalidOperationException("Database connection string is not configured.");
            return new SqlConnection(cs);
        }

        // ============================================
        // EXITED CARS METHODS - ✅ UPDATED FOR DATE RANGE
        // ============================================
        public async Task<List<DailyCollection>> GetDailyCollectionData(DateTime startDate, DateTime endDate)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    // Filter by created_on only (as requested).
                    // LEFT JOIN to transactions table to get revalidation date/time/charges.
                    var query = @"
        SELECT
            e.[id], e.[customer_id], e.[cardno], e.[card_name], e.[card_type], e.[car_plate_no],
            e.[entry_time], e.[exit_time], e.[fee_schedule_name], e.[park_time_total],
            e.[charges], e.[discount_amount], e.[balance_to_collect], e.[paid_amount],
            e.[pay_mode], e.[payment_source], e.[pay_source],
            e.[validation_time], e.[remark], e.[reason],
            e.[transaction_id], e.[transaction_status], e.[addn_chgs], e.[created_on],
            t.[revalidation_time], t.[revalidation_charges]
        FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited] e
        LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_19_transactions] t
            ON t.[transaction_id] = e.[transaction_id]
           AND t.[revalidation_time] IS NOT NULL
        WHERE CAST(e.[created_on] AS DATE) >= @startDate
          AND CAST(e.[created_on] AS DATE) <= @endDate
        ORDER BY e.[created_on] DESC";

                    var result = (await connection.QueryAsync<DailyCollection>(
                        query,
                        new { startDate = startDate.Date, endDate = endDate.Date }
                    )).ToList();

                    return result;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching daily collection: {ex.Message}");
            }
        }

        public async Task<List<dynamic>> GetPaymentMethodSummary(DateTime startDate, DateTime endDate)
        {
            try
            {
                var exitedCards = await GetDailyCollectionData(startDate, endDate);

                if (exitedCards.Count == 0)
                {
                    return CreateEmptySummary();
                }

                var devices = new[] { "Server", "Paystation1", "Paystation2", "Paystation3", "Paystation4", "HHD1", "HHD2", "HHD3", "HHD4", "Web" };
                var paymentModes = new[] { "Cash", "M-Pesa", "M-Pesa Via Paybill", "Free of Charge" };
                var summaryRows = new List<dynamic>();

                foreach (var payMode in paymentModes)
                {
                    var deviceAmounts = new Dictionary<string, decimal>();
                    var deviceCounts = new Dictionary<string, int>();

                    foreach (var device in devices)
                    {
                        var records = exitedCards
                            .Where(x =>
                            {
                                string sourceValue = !string.IsNullOrWhiteSpace(x.pay_source)
                                    ? x.pay_source.Trim()
                                    : (!string.IsNullOrWhiteSpace(x.payment_source)
                                        ? x.payment_source.Trim()
                                        : "Server");

                                string payModeValue = !string.IsNullOrWhiteSpace(x.pay_mode)
                                    ? x.pay_mode.Trim()
                                    : "Cash";

                                string normalizedMode = NormalizePayMode(payModeValue);

                                bool sourceMatch = sourceValue.Equals(device, StringComparison.OrdinalIgnoreCase);
                                bool modeMatch = normalizedMode.Equals(payMode, StringComparison.OrdinalIgnoreCase);

                                return sourceMatch && modeMatch;
                            })
                            .ToList();

                        int count = records.Count;
                        decimal amount = records.Sum(x =>
                            // ✅ FOC = charges column; everything else = paid_amount
                            (payMode.Equals("Free of Charge", StringComparison.OrdinalIgnoreCase))
                                ? (x.charges ?? 0m)
                                : (x.paid_amount ?? 0m));

                        deviceCounts[device] = count;
                        deviceAmounts[device] = amount;
                    }

                    decimal totalAmount = deviceAmounts.Values.Sum();

                    summaryRows.Add(new
                    {
                        PayMode = payMode,
                        ServerCount = deviceCounts["Server"],
                        Server = deviceAmounts["Server"],
                        Paystation1Count = deviceCounts["Paystation1"],
                        Paystation1 = deviceAmounts["Paystation1"],
                        Paystation2Count = deviceCounts["Paystation2"],
                        Paystation2 = deviceAmounts["Paystation2"],
                        Paystation3Count = deviceCounts["Paystation3"],
                        Paystation3 = deviceAmounts["Paystation3"],
                        Paystation4Count = deviceCounts["Paystation4"],
                        Paystation4 = deviceAmounts["Paystation4"],
                        HHD1Count = deviceCounts["HHD1"],
                        HHD1 = deviceAmounts["HHD1"],
                        HHD2Count = deviceCounts["HHD2"],
                        HHD2 = deviceAmounts["HHD2"],
                        HHD3Count = deviceCounts["HHD3"],
                        HHD3 = deviceAmounts["HHD3"],
                        HHD4Count = deviceCounts["HHD4"],
                        HHD4 = deviceAmounts["HHD4"],
                        WebCount = deviceCounts["Web"],
                        Web = deviceAmounts["Web"],
                        Total = totalAmount
                    });
                }

                return summaryRows;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching payment summary: {ex.Message}");
            }
        }

        // ============================================
        // ✅ FIXED: GET CARD TYPE SUMMARY (SEASONAL & VIP) - UPDATED FOR DATE RANGE
        // ============================================
        public async Task<List<dynamic>> GetCardTypeSummary(DateTime startDate, DateTime endDate)
        {
            try
            {
                var exitedCards = await GetDailyCollectionData(startDate, endDate);

                if (exitedCards.Count == 0)
                {
                    return new List<dynamic>
            {
                new { CardType = "Seasonal", Count = 0 },
                new { CardType = "VIP", Count = 0 }
            };
                }

                var devices = new[] { "Server", "Paystation1", "Paystation2", "Paystation3", "Paystation4",
                              "HHD1", "HHD2", "HHD3", "HHD4", "Web" };

                var cardTypes = new[] { "Seasonal", "VIP" };
                var summaryRows = new List<dynamic>();

                foreach (var cardType in cardTypes)
                {
                    var deviceCounts = new Dictionary<string, int>();

                    foreach (var device in devices)
                    {
                        var count = exitedCards
                            .Where(x =>
                            {
                                // ✅ FIXED: Use payment_source (with underscore)
                                var source = !string.IsNullOrWhiteSpace(x.payment_source)
                                    ? x.payment_source
                                    : "Server";  // Default to Server if empty

                                var normalizedSource = NormalizePaymentSource(source);
                                var normalizedCardType = NormalizeCardType(x.card_type);

                                return normalizedSource.Equals(device, StringComparison.OrdinalIgnoreCase) &&
                                       normalizedCardType.Equals(cardType, StringComparison.OrdinalIgnoreCase);
                            })
                            .Count();

                        deviceCounts[device] = count;
                    }

                    int totalCount = deviceCounts.Values.Sum();

                    summaryRows.Add(new
                    {
                        CardType = cardType,
                        ServerCount = deviceCounts["Server"],
                        Paystation1Count = deviceCounts["Paystation1"],
                        Paystation2Count = deviceCounts["Paystation2"],
                        Paystation3Count = deviceCounts["Paystation3"],
                        Paystation4Count = deviceCounts["Paystation4"],
                        HHD1Count = deviceCounts["HHD1"],
                        HHD2Count = deviceCounts["HHD2"],
                        HHD3Count = deviceCounts["HHD3"],
                        HHD4Count = deviceCounts["HHD4"],
                        WebCount = deviceCounts["Web"],
                        TotalCount = totalCount
                    });
                }

                return summaryRows;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching card type summary: {ex.Message}");
            }
        }

        // ✅ Helper method to normalize card types
        private string NormalizeCardType(string cardType)
        {
            if (string.IsNullOrWhiteSpace(cardType))
                return "Unknown";

            string normalized = cardType.Trim().ToUpperInvariant();

            if (normalized.Contains("SEASONAL"))
                return "Seasonal";
            if (normalized.Contains("VIP"))
                return "VIP";
            if (normalized.Contains("TEMP") || normalized.Contains("HOURLY"))
                return "Temporary";

            return cardType.Trim();
        }

        public async Task<object> GetDailySummaryStatistics(DateTime startDate, DateTime endDate)
        {
            try
            {
                var exitedCards = await GetDailyCollectionData(startDate, endDate);

                if (exitedCards.Count == 0)
                {
                    return CreateEmptyStatistics();
                }

                var devices = new[] { "Server", "Paystation1", "Paystation2", "Paystation3", "Paystation4", "HHD1", "HHD2", "HHD3", "HHD4", "Web" };
                var deviceStats = new Dictionary<string, object>();

                foreach (var device in devices)
                {
                    var deviceRecords = exitedCards.Where(x =>
                    {
                        string sourceValue = !string.IsNullOrWhiteSpace(x.pay_source)
                            ? x.pay_source.Trim()
                            : (!string.IsNullOrWhiteSpace(x.payment_source)
                                ? x.payment_source.Trim()
                                : "Server");
                        return sourceValue.Equals(device, StringComparison.OrdinalIgnoreCase);
                    }).ToList();

                    var cashRecords = deviceRecords.Where(x =>
                        NormalizePayMode(x.pay_mode ?? "").Equals("Cash", StringComparison.OrdinalIgnoreCase)
                    ).ToList();

                    var mPesaRecords = deviceRecords.Where(x =>
                        NormalizePayMode(x.pay_mode ?? "").Equals("M-Pesa", StringComparison.OrdinalIgnoreCase)
                    ).ToList();

                    var mPesaPaybillRecords = deviceRecords.Where(x =>
                        NormalizePayMode(x.pay_mode ?? "").Equals("M-Pesa Via Paybill", StringComparison.OrdinalIgnoreCase)
                    ).ToList();

                    var focRecords = deviceRecords.Where(x =>
                        NormalizePayMode(x.pay_mode ?? "").Equals("Free of Charge", StringComparison.OrdinalIgnoreCase) ||
                        NormalizePayMode(x.pay_mode ?? "").Equals("FOC", StringComparison.OrdinalIgnoreCase)
                    ).ToList();

                    deviceStats[device] = new
                    {
                        cashCount = cashRecords.Count,
                        cashTotal = cashRecords.Sum(x => x.paid_amount ?? 0m),
                        mPesaCount = mPesaRecords.Count,
                        mPesaTotal = mPesaRecords.Sum(x => x.paid_amount ?? 0m),
                        mPesaViaPaybillCount = mPesaPaybillRecords.Count,
                        mPesaViaPaybillTotal = mPesaPaybillRecords.Sum(x => x.paid_amount ?? 0m),
                        focCount = focRecords.Count,
                        // ✅ FOC amount = charges column (the amount that was waived)
                        focTotal = focRecords.Sum(x => x.charges ?? 0m)
                    };
                }

                var totalCashRecords = exitedCards.Where(x =>
                    NormalizePayMode(x.pay_mode ?? "").Equals("Cash", StringComparison.OrdinalIgnoreCase)
                ).ToList();

                var totalMPesaRecords = exitedCards.Where(x =>
                    NormalizePayMode(x.pay_mode ?? "").Equals("M-Pesa", StringComparison.OrdinalIgnoreCase)
                ).ToList();

                var totalMPesaPaybillRecords = exitedCards.Where(x =>
                    NormalizePayMode(x.pay_mode ?? "").Equals("M-Pesa Via Paybill", StringComparison.OrdinalIgnoreCase)
                ).ToList();

                var totalFocRecords = exitedCards.Where(x =>
                    NormalizePayMode(x.pay_mode ?? "").Equals("Free of Charge", StringComparison.OrdinalIgnoreCase) ||
                    NormalizePayMode(x.pay_mode ?? "").Equals("FOC", StringComparison.OrdinalIgnoreCase)
                ).ToList();

                var stats = new
                {
                    devices = deviceStats,
                    totalVehicles = exitedCards.Count,
                    totalCharges = exitedCards.Sum(x => x.charges ?? 0m),
                    totalCashCount = totalCashRecords.Count,
                    totalCashCollected = totalCashRecords.Sum(x => x.paid_amount ?? 0m),
                    totalMPesaCount = totalMPesaRecords.Count,
                    totalMPesaCollected = totalMPesaRecords.Sum(x => x.paid_amount ?? 0m),
                    totalMPesaViaPaybillCount = totalMPesaPaybillRecords.Count,
                    totalMPesaViaPaybillCollected = totalMPesaPaybillRecords.Sum(x => x.paid_amount ?? 0m),
                    totalFocCount = totalFocRecords.Count,
                    // ✅ FOC amount = charges column
                    totalFocAmount = totalFocRecords.Sum(x => x.charges ?? 0m),
                    creditTakenCount = 0,
                    equityCount = 0,
                    advancePaidCount = 0,
                    reparkCount = 0,
                    grandTotal = exitedCards.Count,
                    grandTotalAmount = totalCashRecords.Sum(x => x.paid_amount ?? 0m)
                                      + totalMPesaRecords.Sum(x => x.paid_amount ?? 0m)
                                      + totalMPesaPaybillRecords.Sum(x => x.paid_amount ?? 0m)
                };

                return stats;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching daily summary statistics: {ex.Message}");
            }
        }

        public async Task<DataTable> ExportDailyToDataTable(DateTime selectedDate)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    var query = @"
        SELECT
            e.id                                                   AS [#],
            e.transaction_id                                       AS [Transaction ID],
            e.cardno                                               AS [Card Number],
            e.card_name                                            AS [Card Name],
            e.car_plate_no                                         AS [Vehicle Plate],
            FORMAT(e.entry_time, 'dd/MM/yyyy HH:mm:ss')           AS [Entry Time],
            FORMAT(e.exit_time,  'dd/MM/yyyy HH:mm:ss')           AS [Exit Time],
            e.park_time_total                                      AS [Park Time],
            ISNULL(e.charges, 0)                                   AS [Charge (KES)],
            ISNULL(e.paid_amount, 0)                               AS [Paid (KES)],
            ISNULL(e.discount_amount, 0)                           AS [Discount (KES)],
            e.pay_mode                                             AS [Pay Mode],
            FORMAT(e.validation_time, 'dd/MM/yyyy HH:mm:ss')      AS [Validation DateTime],
            FORMAT(t.revalidation_time, 'dd/MM/yyyy HH:mm:ss')    AS [Revalidation DateTime],
            ISNULL(t.revalidation_charges, 0)                      AS [Revalidation Charge (KES)],
            e.transaction_status                                   AS [Status],
            e.remark                                               AS [Remarks]
        FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited] e
        LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_19_transactions] t
            ON t.[transaction_id] = e.[transaction_id]
           AND t.[revalidation_time] IS NOT NULL
        WHERE CAST(e.[created_on] AS DATE) = @SelectedDate
        ORDER BY e.[created_on] DESC";

                    var dt = new DataTable();
                    using (var cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@SelectedDate", selectedDate.Date);
                        using (var adapter = new SqlDataAdapter(cmd))
                        {
                            adapter.Fill(dt);
                        }
                    }
                    return dt;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error exporting daily collection: {ex.Message}");
            }
        }

        // ============================================
        // UPDATED METHOD - REPLACE IN CollectionService.cs
        // (Only ExitedData CTE bucket logic changed; rest of file unaffected)
        // ============================================
        public async Task<List<dynamic>> GetMonthlyCollectionData(int year, int month)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    var query = @"
WITH VehicleData AS (
    SELECT
        CAST([created_on] AS DATE) AS EntryDate,
        COUNT(*) AS TotalVehicles
    FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited]
    WHERE YEAR([created_on]) = @Year AND MONTH([created_on]) = @Month
    GROUP BY CAST([created_on] AS DATE)
),
RevalidationData AS (
    SELECT
        CAST([created_on] AS DATE) AS RevalDate,
        COUNT(*) AS RevalCount,
        SUM(ISNULL([revalidation_charges], 0)) AS RevalAmount
    FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
    WHERE YEAR([created_on]) = @Year AND MONTH([created_on]) = @Month
      AND [revalidation_time] IS NOT NULL
    GROUP BY CAST([created_on] AS DATE)
),
ExitedData AS (
    SELECT
        CAST([created_on] AS DATE) AS ExitDate,
        COUNT(*) AS TotalExited,

        COUNT(CASE WHEN LTRIM(RTRIM(ISNULL([pay_mode], 'Cash'))) = 'Cash' THEN 1 END) AS CashCount,
        SUM(CASE WHEN LTRIM(RTRIM(ISNULL([pay_mode], 'Cash'))) = 'Cash'
                 THEN ISNULL([paid_amount], 0) ELSE 0 END) AS CashTotal,

        COUNT(CASE WHEN LTRIM(RTRIM(UPPER(REPLACE(REPLACE(REPLACE([pay_mode], ' ', ''), '-', ''), '_', ''))))
                        IN ('MPESA', 'MPESAKIAPAYBILL') THEN 1 END) AS MPesaCount,
        SUM(CASE WHEN LTRIM(RTRIM(UPPER(REPLACE(REPLACE(REPLACE([pay_mode], ' ', ''), '-', ''), '_', ''))))
                        IN ('MPESA', 'MPESAKIAPAYBILL')
                 THEN ISNULL([paid_amount], 0) ELSE 0 END) AS MPesaTotal,

        COUNT(CASE WHEN LTRIM(RTRIM(UPPER(ISNULL([pay_mode], '')))) IN ('FOC', 'FREE OF CHARGE') THEN 1 END) AS FOCCount,
        SUM(CASE WHEN LTRIM(RTRIM(UPPER(ISNULL([pay_mode], '')))) IN ('FOC', 'FREE OF CHARGE')
                 THEN ISNULL([charges], 0) ELSE 0 END) AS FOCAmount,

        COUNT(CASE WHEN LTRIM(RTRIM(UPPER(ISNULL([pay_source], '')))) = 'HHD4'
                    AND LTRIM(RTRIM(UPPER(ISNULL([pay_mode], '')))) IN ('FOC', 'FREE OF CHARGE') THEN 1 END) AS CtmFocCount,
        SUM(CASE WHEN LTRIM(RTRIM(UPPER(ISNULL([pay_source], '')))) = 'HHD4'
                    AND LTRIM(RTRIM(UPPER(ISNULL([pay_mode], '')))) IN ('FOC', 'FREE OF CHARGE')
                 THEN ISNULL([charges], 0) ELSE 0 END) AS CtmFocAmount,

        COUNT(CASE WHEN ISNULL([charges], 0) = 0
                    AND LTRIM(RTRIM(UPPER(ISNULL([pay_mode], '')))) NOT IN ('FOC', 'FREE OF CHARGE')
                    AND (UPPER(ISNULL([card_type], '')) LIKE '%TEMP%' OR UPPER(ISNULL([card_type], '')) LIKE '%HOURLY%')
               THEN 1 END) AS TempZeroChargeCount,
        COUNT(CASE WHEN ISNULL([charges], 0) > 0
                    AND (UPPER(ISNULL([card_type], '')) LIKE '%TEMP%' OR UPPER(ISNULL([card_type], '')) LIKE '%HOURLY%')
               THEN 1 END) AS TempChargedCount,
        SUM(CASE WHEN (UPPER(ISNULL([card_type], '')) LIKE '%TEMP%' OR UPPER(ISNULL([card_type], '')) LIKE '%HOURLY%')
                 THEN ISNULL([paid_amount], 0) ELSE 0 END) AS TempPaidAmount,

        COUNT(CASE WHEN ISNULL([charges], 0) = 0
                    AND LTRIM(RTRIM(UPPER(ISNULL([pay_mode], '')))) NOT IN ('FOC', 'FREE OF CHARGE')
                    AND UPPER(ISNULL([card_type], '')) LIKE '%SEASON%'
               THEN 1 END) AS FocPerUnitZeroCount,
        COUNT(CASE WHEN ISNULL([charges], 0) > 0
                    AND UPPER(ISNULL([card_type], '')) LIKE '%SEASON%'
               THEN 1 END) AS FocPerUnitChargedCount,
        SUM(CASE WHEN UPPER(ISNULL([card_type], '')) LIKE '%SEASON%'
                 THEN ISNULL([paid_amount], 0) ELSE 0 END) AS FocPerUnitPaidAmount,

        COUNT(CASE WHEN ISNULL([charges], 0) = 0
                    AND LTRIM(RTRIM(UPPER(ISNULL([pay_mode], '')))) NOT IN ('FOC', 'FREE OF CHARGE')
                    AND UPPER(ISNULL([card_type], '')) LIKE '%DAILY%ACCESS%'
               THEN 1 END) AS DailyAccessZeroCount,
        COUNT(CASE WHEN ISNULL([charges], 0) > 0
                    AND UPPER(ISNULL([card_type], '')) LIKE '%DAILY%ACCESS%'
               THEN 1 END) AS DailyAccessChargedCount,
        SUM(CASE WHEN UPPER(ISNULL([card_type], '')) LIKE '%DAILY%ACCESS%'
                 THEN ISNULL([paid_amount], 0) ELSE 0 END) AS DailyAccessPaidAmount,

        COUNT(CASE WHEN ISNULL([charges], 0) = 0
                    AND LTRIM(RTRIM(UPPER(ISNULL([pay_mode], '')))) NOT IN ('FOC', 'FREE OF CHARGE')
                    AND UPPER(ISNULL([card_type], '')) LIKE '%CASH%CARD%'
               THEN 1 END) AS CashCardZeroCount,
        COUNT(CASE WHEN ISNULL([charges], 0) > 0
                    AND UPPER(ISNULL([card_type], '')) LIKE '%CASH%CARD%'
               THEN 1 END) AS CashCardChargedCount,
        SUM(CASE WHEN UPPER(ISNULL([card_type], '')) LIKE '%CASH%CARD%'
                 THEN ISNULL([paid_amount], 0) ELSE 0 END) AS CashCardPaidAmount,

        COUNT(CASE WHEN UPPER(ISNULL([card_type], '')) LIKE '%VIP%' THEN 1 END) AS VipCount,

        COUNT(CASE WHEN ISNULL([charges], 0) = 0
                    AND LTRIM(RTRIM(UPPER(ISNULL([pay_mode], '')))) NOT IN ('FOC', 'FREE OF CHARGE')
               THEN 1 END) AS ZeroChargeCount,

        COUNT(CASE WHEN UPPER(ISNULL([card_type], '')) LIKE '%PERMANENT%'
                     OR UPPER(ISNULL([card_type], '')) LIKE '%SEASON%'
               THEN 1 END) AS PermanentParkersCount,

        SUM(ISNULL([discount_amount], 0)) AS TotalDiscount,
        SUM(ISNULL([paid_amount], 0))     AS TotalPaid,
        SUM(ISNULL([charges], 0))         AS TotalChargedAmount
    FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited]
    WHERE YEAR([created_on]) = @Year AND MONTH([created_on]) = @Month
    GROUP BY CAST([created_on] AS DATE)
)
SELECT
    E.ExitDate                           AS Date,
    ISNULL(V.TotalVehicles, 0)           AS TotalVehicles,
    E.TotalExited                        AS TotalTransactions,
    E.CashCount,
    E.CashTotal,
    E.MPesaCount,
    E.MPesaTotal,
    E.FOCCount,
    E.FOCAmount,
    E.CtmFocCount,
    E.CtmFocAmount,
    E.TempZeroChargeCount,
    E.TempChargedCount,
    E.TempPaidAmount,
    E.FocPerUnitZeroCount,
    E.FocPerUnitChargedCount,
    E.FocPerUnitPaidAmount,
    E.DailyAccessZeroCount,
    E.DailyAccessChargedCount,
    E.DailyAccessPaidAmount,
    E.CashCardZeroCount,
    E.CashCardChargedCount,
    E.CashCardPaidAmount,
    E.VipCount,
    E.ZeroChargeCount,
    E.PermanentParkersCount,
    E.TotalDiscount,
    E.TotalPaid,
    E.TotalChargedAmount,
    ISNULL(R.RevalCount, 0)              AS RevalidationCount,
    ISNULL(R.RevalAmount, 0)             AS RevalidationAmount
FROM ExitedData E
LEFT JOIN VehicleData V ON E.ExitDate = V.EntryDate
LEFT JOIN RevalidationData R ON E.ExitDate = R.RevalDate
ORDER BY E.ExitDate;";

                    var result = await connection.QueryAsync<dynamic>(query, new { Year = year, Month = month });

                    return result.Select(r => new
                    {
                        date = ((DateTime)r.Date).ToString("yyyy-MM-dd"),
                        totalVehicles = (int)r.TotalVehicles,
                        totalTransactions = (int)r.TotalTransactions,
                        cashCount = (int)r.CashCount,
                        cashTotal = (decimal)r.CashTotal,
                        mPesaCount = (int)r.MPesaCount,
                        mPesaTotal = (decimal)r.MPesaTotal,
                        focCount = (int)r.FOCCount,
                        focAmount = (decimal)r.FOCAmount,
                        ctmFocCount = (int)r.CtmFocCount,
                        ctmFocAmount = (decimal)r.CtmFocAmount,
                        tempZeroChargeCount = (int)r.TempZeroChargeCount,
                        tempChargedCount = (int)r.TempChargedCount,
                        tempPaidAmount = (decimal)r.TempPaidAmount,
                        focPerUnitZeroCount = (int)r.FocPerUnitZeroCount,
                        focPerUnitChargedCount = (int)r.FocPerUnitChargedCount,
                        focPerUnitPaidAmount = (decimal)r.FocPerUnitPaidAmount,
                        dailyAccessZeroCount = (int)r.DailyAccessZeroCount,
                        dailyAccessChargedCount = (int)r.DailyAccessChargedCount,
                        dailyAccessPaidAmount = (decimal)r.DailyAccessPaidAmount,
                        cashCardZeroCount = (int)r.CashCardZeroCount,
                        cashCardChargedCount = (int)r.CashCardChargedCount,
                        cashCardPaidAmount = (decimal)r.CashCardPaidAmount,
                        vipCount = (int)r.VipCount,
                        totalCharges = (decimal)r.TotalChargedAmount,
                        totalPaid = (decimal)(r.TotalPaid ?? 0),
                        discount = (decimal)(r.TotalDiscount ?? 0),
                        zeroChargeCount = (int)(r.ZeroChargeCount ?? 0),
                        permanentParkersCount = (int)(r.PermanentParkersCount ?? 0),
                        revalidationCount = (int)r.RevalidationCount,
                        revalidationAmount = (decimal)r.RevalidationAmount,
                        totalAmount = (decimal)r.CashTotal + (decimal)r.MPesaTotal
                    }).ToList<dynamic>();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching monthly exited cars collection: {ex.Message}");
            }
        }

        public async Task<DataTable> ExportMonthlyToDataTable(int year, int month)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    var query = @"
SELECT
    FORMAT(CAST(e.created_on AS DATE), 'dd/MM/yyyy')    AS [Date],
    DATENAME(DW, e.created_on)                           AS [Day],
    COUNT(DISTINCT e.car_plate_no)                       AS [Total Vehicles],
    ISNULL(SUM(e.charges), 0)                            AS [Total Charges (KES)],
    ISNULL(SUM(e.paid_amount), 0)                        AS [Total Paid (KES)],
    ISNULL(SUM(e.discount_amount), 0)                    AS [Total Discount (KES)],
    COUNT(CASE WHEN e.pay_mode = 'Cash' THEN 1 END)      AS [Cash Count],
    COUNT(CASE WHEN e.pay_mode LIKE '%M-Pesa%' THEN 1 END) AS [M-Pesa Count],
    -- FOC count and amount from charges column
    COUNT(CASE WHEN UPPER(e.pay_mode) IN ('FREE OF CHARGE', 'FOC') THEN 1 END) AS [FOC Count],
    ISNULL(SUM(CASE WHEN UPPER(e.pay_mode) IN ('FREE OF CHARGE', 'FOC')
                    THEN e.charges ELSE 0 END), 0)       AS [FOC Amount (KES)],
    -- Revalidations for the same day (from transactions table)
    ISNULL(R.RevalCount, 0)                              AS [Revalidation Count],
    ISNULL(R.RevalAmount, 0)                             AS [Revalidation Amount (KES)]
FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited] e
LEFT JOIN (
    SELECT
        CAST([created_on] AS DATE) AS RevalDate,
        COUNT(*)                   AS RevalCount,
        SUM(ISNULL([revalidation_charges], 0)) AS RevalAmount
    FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
    WHERE YEAR([created_on]) = @Year AND MONTH([created_on]) = @Month
      AND [revalidation_time] IS NOT NULL
    GROUP BY CAST([created_on] AS DATE)
) R ON CAST(e.created_on AS DATE) = R.RevalDate
WHERE YEAR(e.created_on) = @Year
  AND MONTH(e.created_on) = @Month
  AND e.created_on IS NOT NULL
GROUP BY CAST(e.created_on AS DATE), DATENAME(DW, e.created_on),
         R.RevalCount, R.RevalAmount
ORDER BY CAST(e.created_on AS DATE) ASC";

                    var dt = new DataTable();
                    using (var cmd = new SqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@Year", year);
                        cmd.Parameters.AddWithValue("@Month", month);
                        using (var adapter = new SqlDataAdapter(cmd))
                        {
                            adapter.Fill(dt);
                        }
                    }
                    return dt;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error exporting monthly collection: {ex.Message}");
            }
        }

        // ============================================
        // TRANSACTION METHODS - ✅ UPDATED FOR DATE RANGE
        // ============================================
        public async Task<List<TransactionData>> GetTransactionSummary(DateTime startDate, DateTime endDate)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    var query = @"
                SELECT 
                    [id], [transaction_id], [car_plate_no], [cardno], [cardname],
                    [transaction_type], [paidamount], [operator_name], [transaction_status],
                    [payment_source], [status], [created_by], [created_on],
                    [validation_time], [validation_charges], [validation_paymode],
                    [revalidation_time], [revalidation_charges], [revalidation_paymode],
                    [entry_time]
                FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
                WHERE CAST([created_on] AS DATE) >= @startDate 
                  AND CAST([created_on] AS DATE) <= @endDate
                ORDER BY [created_on] DESC";

                    var result = (await connection.QueryAsync<TransactionData>(
                        query,
                        new { startDate = startDate.Date, endDate = endDate.Date }
                    )).ToList();

                    return result;
                }
            }
            catch (Exception ex)
            {
                return new List<TransactionData>();
            }
        }
        private (string source, string payMode) ParseTopUpPaymentSource(string paymentSource)
        {
            if (string.IsNullOrWhiteSpace(paymentSource))
                return ("Server", "");

            // Match pattern: Source(PayMode)
            var match = System.Text.RegularExpressions.Regex.Match(paymentSource, @"^(.+?)\((.+?)\)$");

            if (match.Success)
            {
                string source = match.Groups[1].Value.Trim();
                string payMode = match.Groups[2].Value.Trim();
                return (source, payMode);
            }

            // If no match, return original as source
            return (paymentSource.Trim(), "");
        }

        public async Task<List<dynamic>> GetTransactionPaymentMethodSummary(DateTime startDate, DateTime endDate)
        {
            try
            {
                var transactions = await GetTransactionSummary(startDate, endDate);

                if (transactions.Count == 0)
                    return CreateEmptySummary();

                var devices = new[] { "Server", "Paystation1", "Paystation2", "Paystation3", "Paystation4",
                     "HHD1", "HHD2", "HHD3", "HHD4", "Web" };
                var paymentModes = new[] { "Cash", "M-Pesa", "M-Pesa Via Paybill", "Free of Charge" };

                var summaryRows = new List<dynamic>();

                foreach (var payMode in paymentModes)
                {
                    var deviceAmounts = new Dictionary<string, decimal>();
                    var deviceCounts = new Dictionary<string, int>();

                    foreach (var device in devices)
                    {
                        // ✅ VALIDATION TRANSACTIONS
                        var validationRecords = transactions
                            .Where(x => x.validation_time != null
                                     && x.validation_time.Value.Date >= startDate.Date
                                     && x.validation_time.Value.Date <= endDate.Date
                                     && !string.IsNullOrWhiteSpace(x.validation_paymode))
                            .Where(x =>
                            {
                                string sourceValue = !string.IsNullOrWhiteSpace(x.payment_source)
                                                    ? x.payment_source.Trim()
                                                    : "Server";
                                string normalizedMode = NormalizePayMode(x.validation_paymode);

                                return sourceValue.Equals(device, StringComparison.OrdinalIgnoreCase)
                                    && normalizedMode.Equals(payMode, StringComparison.OrdinalIgnoreCase);
                            })
                            .ToList();

                        // ✅ REVALIDATION TRANSACTIONS
                        var revalidationRecords = transactions
                            .Where(x => x.revalidation_time != null
                                     && x.revalidation_time.Value.Date >= startDate.Date
                                     && x.revalidation_time.Value.Date <= endDate.Date
                                     && !string.IsNullOrWhiteSpace(x.revalidation_paymode))
                            .Where(x =>
                            {
                                string sourceValue = !string.IsNullOrWhiteSpace(x.payment_source)
                                                    ? x.payment_source.Trim()
                                                    : "Server";
                                string normalizedMode = NormalizePayMode(x.revalidation_paymode);

                                return sourceValue.Equals(device, StringComparison.OrdinalIgnoreCase)
                                    && normalizedMode.Equals(payMode, StringComparison.OrdinalIgnoreCase);
                            })
                            .ToList();

                        // ✅ NEW: TOP-UP TRANSACTIONS
                        var topUpRecords = transactions
                            .Where(x => !string.IsNullOrWhiteSpace(x.transaction_type)
                                     && x.transaction_type.Trim().Equals("TOP-UP", StringComparison.OrdinalIgnoreCase)
                                     && x.created_on.HasValue
                                     && x.created_on.Value.Date >= startDate.Date
                                     && x.created_on.Value.Date <= endDate.Date)
                            .Where(x =>
                            {
                                // Parse the payment_source to extract source and paymode
                                var (source, parsedPayMode) = ParseTopUpPaymentSource(x.payment_source);

                                // Normalize the payment source
                                string normalizedSource = NormalizePaymentSource(source);
                                string normalizedMode = NormalizePayMode(parsedPayMode);

                                return normalizedSource.Equals(device, StringComparison.OrdinalIgnoreCase)
                                    && normalizedMode.Equals(payMode, StringComparison.OrdinalIgnoreCase);
                            })
                            .ToList();

                        int count = validationRecords.Count + revalidationRecords.Count + topUpRecords.Count;

                        // ✅ Sum amounts from all three transaction types
                        decimal amount = validationRecords.Sum(x => x.validation_charges ?? 0m)
                                       + revalidationRecords.Sum(x => x.revalidation_charges ?? 0m)
                                       + topUpRecords.Sum(x => x.paidamount ?? 0m);

                        deviceCounts[device] = count;
                        deviceAmounts[device] = amount;
                    }

                    decimal totalAmount = deviceAmounts.Values.Sum();

                    summaryRows.Add(new
                    {
                        PayMode = payMode,
                        ServerCount = deviceCounts["Server"],
                        Server = deviceAmounts["Server"],
                        Paystation1Count = deviceCounts["Paystation1"],
                        Paystation1 = deviceAmounts["Paystation1"],
                        Paystation2Count = deviceCounts["Paystation2"],
                        Paystation2 = deviceAmounts["Paystation2"],
                        Paystation3Count = deviceCounts["Paystation3"],
                        Paystation3 = deviceAmounts["Paystation3"],
                        Paystation4Count = deviceCounts["Paystation4"],
                        Paystation4 = deviceAmounts["Paystation4"],
                        HHD1Count = deviceCounts["HHD1"],
                        HHD1 = deviceAmounts["HHD1"],
                        HHD2Count = deviceCounts["HHD2"],
                        HHD2 = deviceAmounts["HHD2"],
                        HHD3Count = deviceCounts["HHD3"],
                        HHD3 = deviceAmounts["HHD3"],
                        HHD4Count = deviceCounts["HHD4"],
                        HHD4 = deviceAmounts["HHD4"],
                        WebCount = deviceCounts["Web"],
                        Web = deviceAmounts["Web"],
                        Total = totalAmount
                    });
                }

                return summaryRows;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching transaction payment summary: {ex.Message}");
            }
        }

        public async Task<dynamic> GetTransactionStatistics(DateTime startDate, DateTime endDate)
        {
            try
            {
                using var connection = GetConnection();

                // Updated query to include TOP-UP transactions
                var query = @"
WITH Tx AS
(
    SELECT
        validation_paymode,
        revalidation_paymode,
        payment_source,
        transaction_type,
        paidamount,
        validation_charges,
        revalidation_charges,
        CASE 
            WHEN ISNULL(validation_charges, 0) <> 0 THEN validation_charges
            WHEN ISNULL(revalidation_charges, 0) <> 0 THEN revalidation_charges
            WHEN UPPER(ISNULL(transaction_type, '')) = 'TOP-UP' THEN ISNULL(paidamount, 0)
            ELSE 0
        END AS ChargeAmount,
        -- Extract source and paymode from TOP-UP payment_source
        CASE 
            WHEN UPPER(ISNULL(transaction_type, '')) = 'TOP-UP' 
                 AND CHARINDEX('(', payment_source) > 0
            THEN SUBSTRING(payment_source, 1, CHARINDEX('(', payment_source) - 1)
            ELSE payment_source
        END AS ExtractedSource,
        CASE 
            WHEN UPPER(ISNULL(transaction_type, '')) = 'TOP-UP' 
                 AND CHARINDEX('(', payment_source) > 0
            THEN REPLACE(REPLACE(SUBSTRING(payment_source, CHARINDEX('(', payment_source) + 1, LEN(payment_source)), ')', ''), ' ', '')
            ELSE NULL
        END AS ExtractedPayMode
    FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
    WHERE CAST(created_on AS DATE) >= @StartDate
      AND CAST(created_on AS DATE) <= @EndDate
      AND created_on IS NOT NULL
),
Norm AS
(
    SELECT
        UPPER(REPLACE(REPLACE(ISNULL(ExtractedSource, 'SERVER'), ' ', ''), '-', '')) AS Device,
        validation_paymode,
        revalidation_paymode,
        transaction_type,
        -- For Validation
        UPPER(REPLACE(REPLACE(REPLACE(ISNULL(validation_paymode, ''), ' ', ''), '-', ''), '_', '')) AS ValModeNorm,
        -- For Revalidation
        UPPER(REPLACE(REPLACE(REPLACE(ISNULL(revalidation_paymode, ''), ' ', ''), '-', ''), '_', '')) AS RevalModeNorm,
        -- For TOP-UP
        UPPER(REPLACE(REPLACE(REPLACE(ISNULL(ExtractedPayMode, ''), ' ', ''), '-', ''), '_', '')) AS TopUpModeNorm,
        ChargeAmount
    FROM Tx
),
Classified AS
(
    SELECT
        Device,
        
        -- Cash: Check all three transaction types
        CASE 
            WHEN ValModeNorm = 'CASH' 
                 OR (ValModeNorm = '' AND RevalModeNorm = 'CASH')
                 OR TopUpModeNorm = 'CASH'
            THEN 1 
            ELSE 0 
        END AS IsCash,
        
        -- M-Pesa: Check all three transaction types (excluding Paybill)
        CASE 
            WHEN (ValModeNorm = 'MPESA' AND CHARINDEX('PAYBILL', UPPER(REPLACE(REPLACE(ISNULL(validation_paymode, ''), ' ', ''), '-', ''))) = 0) 
                 OR (ValModeNorm = '' AND RevalModeNorm = 'MPESA' AND CHARINDEX('PAYBILL', UPPER(REPLACE(REPLACE(ISNULL(revalidation_paymode, ''), ' ', ''), '-', ''))) = 0)
                 OR (TopUpModeNorm = 'MPESA' AND TopUpModeNorm NOT LIKE '%PAYBILL%')
            THEN 1 
            ELSE 0 
        END AS IsMPesa,
        
        -- M-Pesa Paybill: Check all three transaction types
        CASE 
            WHEN CHARINDEX('PAYBILL', UPPER(REPLACE(REPLACE(ISNULL(validation_paymode, ''), ' ', ''), '-', ''))) > 0
                 OR CHARINDEX('PAYBILL', UPPER(REPLACE(REPLACE(ISNULL(revalidation_paymode, ''), ' ', ''), '-', ''))) > 0
                 OR TopUpModeNorm LIKE '%PAYBILL%'
            THEN 1 
            ELSE 0 
        END AS IsMPesaPaybill,
        
        -- FOC: Check all three transaction types
        CASE 
            WHEN ValModeNorm IN ('FOC','FREEOFCHARGE') 
                 OR (ValModeNorm = '' AND RevalModeNorm IN ('FOC','FREEOFCHARGE'))
                 OR TopUpModeNorm IN ('FOC','FREEOFCHARGE')
            THEN 1 
            ELSE 0 
        END AS IsFOC,
        
        ChargeAmount
    FROM Norm
)
SELECT
    -- ========== SERVER ==========
    ISNULL(SUM(CASE WHEN Device = 'SERVER' AND IsCash = 1 THEN 1 ELSE 0 END), 0) AS ServerCashCount,
    ISNULL(SUM(CASE WHEN Device = 'SERVER' AND IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS ServerCashTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'SERVER' AND IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS ServerMPesaCount,
    ISNULL(SUM(CASE WHEN Device = 'SERVER' AND IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS ServerMPesaTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'SERVER' AND IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS ServerMPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN Device = 'SERVER' AND IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS ServerMPesaViaPaybillTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'SERVER' AND IsFOC = 1 THEN 1 ELSE 0 END), 0) AS ServerFocCount,
    ISNULL(SUM(CASE WHEN Device = 'SERVER' AND IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS ServerFocTotal,

    -- ========== PAYSTATION1 ==========
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION1' AND IsCash = 1 THEN 1 ELSE 0 END), 0) AS Paystation1CashCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION1' AND IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation1CashTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION1' AND IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS Paystation1MPesaCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION1' AND IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation1MPesaTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION1' AND IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS Paystation1MPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION1' AND IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation1MPesaViaPaybillTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION1' AND IsFOC = 1 THEN 1 ELSE 0 END), 0) AS Paystation1FocCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION1' AND IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation1FocTotal,

    -- ========== PAYSTATION2 ==========
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION2' AND IsCash = 1 THEN 1 ELSE 0 END), 0) AS Paystation2CashCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION2' AND IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation2CashTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION2' AND IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS Paystation2MPesaCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION2' AND IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation2MPesaTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION2' AND IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS Paystation2MPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION2' AND IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation2MPesaViaPaybillTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION2' AND IsFOC = 1 THEN 1 ELSE 0 END), 0) AS Paystation2FocCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION2' AND IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation2FocTotal,

    -- ========== PAYSTATION3 ==========
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION3' AND IsCash = 1 THEN 1 ELSE 0 END), 0) AS Paystation3CashCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION3' AND IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation3CashTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION3' AND IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS Paystation3MPesaCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION3' AND IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation3MPesaTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION3' AND IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS Paystation3MPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION3' AND IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation3MPesaViaPaybillTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION3' AND IsFOC = 1 THEN 1 ELSE 0 END), 0) AS Paystation3FocCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION3' AND IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation3FocTotal,

    -- ========== PAYSTATION4 ==========
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION4' AND IsCash = 1 THEN 1 ELSE 0 END), 0) AS Paystation4CashCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION4' AND IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation4CashTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION4' AND IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS Paystation4MPesaCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION4' AND IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation4MPesaTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION4' AND IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS Paystation4MPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION4' AND IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation4MPesaViaPaybillTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION4' AND IsFOC = 1 THEN 1 ELSE 0 END), 0) AS Paystation4FocCount,
    ISNULL(SUM(CASE WHEN Device = 'PAYSTATION4' AND IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS Paystation4FocTotal,

    -- ========== HHD1 ==========
    ISNULL(SUM(CASE WHEN Device IN ('HHD1','HHD01') AND IsCash = 1 THEN 1 ELSE 0 END), 0) AS HHD1CashCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD1','HHD01') AND IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD1CashTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD1','HHD01') AND IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS HHD1MPesaCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD1','HHD01') AND IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD1MPesaTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD1','HHD01') AND IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS HHD1MPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD1','HHD01') AND IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD1MPesaViaPaybillTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD1','HHD01') AND IsFOC = 1 THEN 1 ELSE 0 END), 0) AS HHD1FocCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD1','HHD01') AND IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD1FocTotal,

    -- ========== HHD2 ==========
    ISNULL(SUM(CASE WHEN Device IN ('HHD2','HHD02') AND IsCash = 1 THEN 1 ELSE 0 END), 0) AS HHD2CashCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD2','HHD02') AND IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD2CashTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD2','HHD02') AND IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS HHD2MPesaCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD2','HHD02') AND IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD2MPesaTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD2','HHD02') AND IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS HHD2MPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD2','HHD02') AND IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD2MPesaViaPaybillTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD2','HHD02') AND IsFOC = 1 THEN 1 ELSE 0 END), 0) AS HHD2FocCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD2','HHD02') AND IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD2FocTotal,

    -- ========== HHD3 ==========
    ISNULL(SUM(CASE WHEN Device IN ('HHD3','HHD03') AND IsCash = 1 THEN 1 ELSE 0 END), 0) AS HHD3CashCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD3','HHD03') AND IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD3CashTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD3','HHD03') AND IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS HHD3MPesaCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD3','HHD03') AND IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD3MPesaTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD3','HHD03') AND IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS HHD3MPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD3','HHD03') AND IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD3MPesaViaPaybillTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD3','HHD03') AND IsFOC = 1 THEN 1 ELSE 0 END), 0) AS HHD3FocCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD3','HHD03') AND IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD3FocTotal,

    -- ========== HHD4 ==========
    ISNULL(SUM(CASE WHEN Device IN ('HHD4','HHD04') AND IsCash = 1 THEN 1 ELSE 0 END), 0) AS HHD4CashCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD4','HHD04') AND IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD4CashTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD4','HHD04') AND IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS HHD4MPesaCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD4','HHD04') AND IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD4MPesaTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD4','HHD04') AND IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS HHD4MPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD4','HHD04') AND IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD4MPesaViaPaybillTotal,
    
    ISNULL(SUM(CASE WHEN Device IN ('HHD4','HHD04') AND IsFOC = 1 THEN 1 ELSE 0 END), 0) AS HHD4FocCount,
    ISNULL(SUM(CASE WHEN Device IN ('HHD4','HHD04') AND IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS HHD4FocTotal,

    -- ========== WEB ==========
    ISNULL(SUM(CASE WHEN Device = 'WEB' AND IsCash = 1 THEN 1 ELSE 0 END), 0) AS WebCashCount,
    ISNULL(SUM(CASE WHEN Device = 'WEB' AND IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS WebCashTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'WEB' AND IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS WebMPesaCount,
    ISNULL(SUM(CASE WHEN Device = 'WEB' AND IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS WebMPesaTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'WEB' AND IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS WebMPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN Device = 'WEB' AND IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS WebMPesaViaPaybillTotal,
    
    ISNULL(SUM(CASE WHEN Device = 'WEB' AND IsFOC = 1 THEN 1 ELSE 0 END), 0) AS WebFocCount,
    ISNULL(SUM(CASE WHEN Device = 'WEB' AND IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS WebFocTotal,

    -- ========== OVERALL TOTALS ==========
    ISNULL(COUNT(*), 0) AS TotalTransactions,
    ISNULL(SUM(ChargeAmount), 0) AS TotalAmount,
    
    ISNULL(SUM(CASE WHEN IsCash = 1 THEN 1 ELSE 0 END), 0) AS TotalCashCount,
    ISNULL(SUM(CASE WHEN IsCash = 1 THEN ChargeAmount ELSE 0 END), 0) AS TotalCashCollected,
    
    ISNULL(SUM(CASE WHEN IsMPesa = 1 THEN 1 ELSE 0 END), 0) AS TotalMPesaCount,
    ISNULL(SUM(CASE WHEN IsMPesa = 1 THEN ChargeAmount ELSE 0 END), 0) AS TotalMPesaCollected,
    
    ISNULL(SUM(CASE WHEN IsMPesaPaybill = 1 THEN 1 ELSE 0 END), 0) AS TotalMPesaViaPaybillCount,
    ISNULL(SUM(CASE WHEN IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END), 0) AS TotalMPesaViaPaybillCollected,
    
    ISNULL(SUM(CASE WHEN IsFOC = 1 THEN 1 ELSE 0 END), 0) AS TotalFocCount,
    ISNULL(SUM(CASE WHEN IsFOC = 1 THEN ChargeAmount ELSE 0 END), 0) AS TotalFocAmount
FROM Classified;
";

                var result = await connection.QueryFirstOrDefaultAsync<dynamic>(query, new
                {
                    StartDate = startDate.Date,
                    EndDate = endDate.Date
                });

                // Return formatted statistics
                return new
                {
                    devices = new
                    {
                        Server = new
                        {
                            cashCount = (int)result.ServerCashCount,
                            cashTotal = (decimal)result.ServerCashTotal,
                            mPesaCount = (int)result.ServerMPesaCount,
                            mPesaTotal = (decimal)result.ServerMPesaTotal,
                            mPesaViaPaybillCount = (int)result.ServerMPesaViaPaybillCount,
                            mPesaViaPaybillTotal = (decimal)result.ServerMPesaViaPaybillTotal,
                            focCount = (int)result.ServerFocCount,
                            focTotal = (decimal)result.ServerFocTotal
                        },
                        Paystation1 = new
                        {
                            cashCount = (int)result.Paystation1CashCount,
                            cashTotal = (decimal)result.Paystation1CashTotal,
                            mPesaCount = (int)result.Paystation1MPesaCount,
                            mPesaTotal = (decimal)result.Paystation1MPesaTotal,
                            mPesaViaPaybillCount = (int)result.Paystation1MPesaViaPaybillCount,
                            mPesaViaPaybillTotal = (decimal)result.Paystation1MPesaViaPaybillTotal,
                            focCount = (int)result.Paystation1FocCount,
                            focTotal = (decimal)result.Paystation1FocTotal
                        },
                        Paystation2 = new
                        {
                            cashCount = (int)result.Paystation2CashCount,
                            cashTotal = (decimal)result.Paystation2CashTotal,
                            mPesaCount = (int)result.Paystation2MPesaCount,
                            mPesaTotal = (decimal)result.Paystation2MPesaTotal,
                            mPesaViaPaybillCount = (int)result.Paystation2MPesaViaPaybillCount,
                            mPesaViaPaybillTotal = (decimal)result.Paystation2MPesaViaPaybillTotal,
                            focCount = (int)result.Paystation2FocCount,
                            focTotal = (decimal)result.Paystation2FocTotal
                        },
                        Paystation3 = new
                        {
                            cashCount = (int)result.Paystation3CashCount,
                            cashTotal = (decimal)result.Paystation3CashTotal,
                            mPesaCount = (int)result.Paystation3MPesaCount,
                            mPesaTotal = (decimal)result.Paystation3MPesaTotal,
                            mPesaViaPaybillCount = (int)result.Paystation3MPesaViaPaybillCount,
                            mPesaViaPaybillTotal = (decimal)result.Paystation3MPesaViaPaybillTotal,
                            focCount = (int)result.Paystation3FocCount,
                            focTotal = (decimal)result.Paystation3FocTotal
                        },
                        Paystation4 = new
                        {
                            cashCount = (int)result.Paystation4CashCount,
                            cashTotal = (decimal)result.Paystation4CashTotal,
                            mPesaCount = (int)result.Paystation4MPesaCount,
                            mPesaTotal = (decimal)result.Paystation4MPesaTotal,
                            mPesaViaPaybillCount = (int)result.Paystation4MPesaViaPaybillCount,
                            mPesaViaPaybillTotal = (decimal)result.Paystation4MPesaViaPaybillTotal,
                            focCount = (int)result.Paystation4FocCount,
                            focTotal = (decimal)result.Paystation4FocTotal
                        },
                        HHD1 = new
                        {
                            cashCount = (int)result.HHD1CashCount,
                            cashTotal = (decimal)result.HHD1CashTotal,
                            mPesaCount = (int)result.HHD1MPesaCount,
                            mPesaTotal = (decimal)result.HHD1MPesaTotal,
                            mPesaViaPaybillCount = (int)result.HHD1MPesaViaPaybillCount,
                            mPesaViaPaybillTotal = (decimal)result.HHD1MPesaViaPaybillTotal,
                            focCount = (int)result.HHD1FocCount,
                            focTotal = (decimal)result.HHD1FocTotal
                        },
                        HHD2 = new
                        {
                            cashCount = (int)result.HHD2CashCount,
                            cashTotal = (decimal)result.HHD2CashTotal,
                            mPesaCount = (int)result.HHD2MPesaCount,
                            mPesaTotal = (decimal)result.HHD2MPesaTotal,
                            mPesaViaPaybillCount = (int)result.HHD2MPesaViaPaybillCount,
                            mPesaViaPaybillTotal = (decimal)result.HHD2MPesaViaPaybillTotal,
                            focCount = (int)result.HHD2FocCount,
                            focTotal = (decimal)result.HHD2FocTotal
                        },
                        HHD3 = new
                        {
                            cashCount = (int)result.HHD3CashCount,
                            cashTotal = (decimal)result.HHD3CashTotal,
                            mPesaCount = (int)result.HHD3MPesaCount,
                            mPesaTotal = (decimal)result.HHD3MPesaTotal,
                            mPesaViaPaybillCount = (int)result.HHD3MPesaViaPaybillCount,
                            mPesaViaPaybillTotal = (decimal)result.HHD3MPesaViaPaybillTotal,
                            focCount = (int)result.HHD3FocCount,
                            focTotal = (decimal)result.HHD3FocTotal
                        },
                        HHD4 = new
                        {
                            cashCount = (int)result.HHD4CashCount,
                            cashTotal = (decimal)result.HHD4CashTotal,
                            mPesaCount = (int)result.HHD4MPesaCount,
                            mPesaTotal = (decimal)result.HHD4MPesaTotal,
                            mPesaViaPaybillCount = (int)result.HHD4MPesaViaPaybillCount,
                            mPesaViaPaybillTotal = (decimal)result.HHD4MPesaViaPaybillTotal,
                            focCount = (int)result.HHD4FocCount,
                            focTotal = (decimal)result.HHD4FocTotal
                        },
                        Web = new
                        {
                            cashCount = (int)result.WebCashCount,
                            cashTotal = (decimal)result.WebCashTotal,
                            mPesaCount = (int)result.WebMPesaCount,
                            mPesaTotal = (decimal)result.WebMPesaTotal,
                            mPesaViaPaybillCount = (int)result.WebMPesaViaPaybillCount,
                            mPesaViaPaybillTotal = (decimal)result.WebMPesaViaPaybillTotal,
                            focCount = (int)result.WebFocCount,
                            focTotal = (decimal)result.WebFocTotal
                        }
                    },
                    totalVehicles = 0,
                    totalCharges = (decimal)result.TotalAmount,
                    totalCashCount = (int)result.TotalCashCount,
                    totalCashCollected = (decimal)result.TotalCashCollected,
                    totalMPesaCount = (int)result.TotalMPesaCount,
                    totalMPesaCollected = (decimal)result.TotalMPesaCollected,
                    totalMPesaViaPaybillCount = (int)result.TotalMPesaViaPaybillCount,
                    totalMPesaViaPaybillCollected = (decimal)result.TotalMPesaViaPaybillCollected,
                    totalFocCount = (int)result.TotalFocCount,
                    totalFocAmount = (decimal)result.TotalFocAmount,
                    creditTakenCount = 0,
                    equityCount = 0,
                    advancePaidCount = 0,
                    reparkCount = 0,
                    grandTotal = (int)result.TotalTransactions,
                    grandTotalAmount = (decimal)result.TotalAmount
                };
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching transaction statistics: {ex.Message}", ex);
            }
        }

        // ============================================
        // MONTHLY TRANSACTIONS DATA (NEW - TAB 2) - NO CHANGES
        // ============================================
        // ============================================
        // UPDATED METHOD #1 - REPLACE GetMonthlyTransactionsDataWithVehicles IN CollectionService.cs
        // This method is used by ExportMonthlyTransactionsExcel
        // ============================================

        public async Task<List<dynamic>> GetMonthlyTransactionsDataWithVehicles(int year, int month)
        {
            try
            {
                using var connection = GetConnection();

                var query = @"
-- 1. Standard Transaction Data (Validations/Revalidations)
WITH TransactionData AS
(
    SELECT
        CAST(created_on AS DATE) AS [Date],
        validation_paymode,
        revalidation_paymode,
        payment_source,
        CASE 
            WHEN ISNULL(validation_charges, 0) <> 0 THEN ISNULL(validation_charges, 0)
            WHEN ISNULL(revalidation_charges, 0) <> 0 THEN ISNULL(revalidation_charges, 0)
            ELSE 0
        END AS ChargeAmount
    FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
    WHERE YEAR(created_on) = @Year AND MONTH(created_on) = @Month
      AND (transaction_type IS NULL OR transaction_type <> 'TOP-UP') -- Standard tx only
),
Norm AS
(
    SELECT
        [Date],
        UPPER(REPLACE(REPLACE(ISNULL(payment_source, 'SERVER'), ' ', ''), '-', '')) AS DeviceRaw,
        UPPER(REPLACE(REPLACE(ISNULL(validation_paymode, revalidation_paymode), ' ', ''), '-', '')) AS ModeNorm,
        ChargeAmount
    FROM TransactionData
),
Classified AS
(
    SELECT
        [Date],
        CASE
            WHEN DeviceRaw IN ('SERVER') THEN 'Server'
            WHEN DeviceRaw IN ('PAYSTATION1','PS1','PAYSTATION01') THEN 'Paystation1'
            WHEN DeviceRaw IN ('PAYSTATION2','PS2','PAYSTATION02') THEN 'Paystation2'
            WHEN DeviceRaw IN ('HHD1','HHD01','HANDHELD1') THEN 'HHD1'
            WHEN DeviceRaw IN ('HHD2','HHD02','HANDHELD2') THEN 'HHD2'
            WHEN DeviceRaw IN ('HHD3','HHD03','HANDHELD3') THEN 'HHD3'
            WHEN DeviceRaw IN ('HHD4','HHD04','HANDHELD4') THEN 'HHD4'
            WHEN DeviceRaw IN ('WEB','ONLINE','WEBSITE') THEN 'Web'
            ELSE 'Server'
        END AS Device,
        CASE WHEN ModeNorm = 'CASH' THEN 1 ELSE 0 END AS IsCash,
        CASE WHEN ModeNorm = 'MPESA' OR (ModeNorm LIKE 'MPESA%' AND ModeNorm NOT LIKE '%PAYBILL%') THEN 1 ELSE 0 END AS IsMPesa,
        CASE WHEN ModeNorm LIKE '%PAYBILL%' THEN 1 ELSE 0 END AS IsMPesaPaybill,
        CASE WHEN ModeNorm IN ('FOC','FREEOFCHARGE') THEN 1 ELSE 0 END AS IsFOC,
        ChargeAmount
    FROM Norm
),
DailyAgg AS
(
    SELECT
        [Date],
        COUNT(*) AS TotalTransactions,
        SUM(CASE WHEN IsCash = 1 THEN 1 ELSE 0 END) AS CashCount,
        SUM(CASE WHEN IsCash = 1 THEN ChargeAmount ELSE 0 END) AS CashTotal,
        SUM(CASE WHEN IsMPesa = 1 OR IsMPesaPaybill = 1 THEN 1 ELSE 0 END) AS MPesaCount,
        SUM(CASE WHEN IsMPesa = 1 OR IsMPesaPaybill = 1 THEN ChargeAmount ELSE 0 END) AS MPesaTotal,
        SUM(CASE WHEN IsFOC = 1 AND Device NOT IN ('HHD4') THEN 1 ELSE 0 END) AS FOCCount,
        SUM(CASE WHEN IsFOC = 1 AND Device NOT IN ('HHD4') THEN ChargeAmount ELSE 0 END) AS FOCAmount,
        SUM(CASE WHEN IsFOC = 1 AND Device IN ('HHD4') THEN 1 ELSE 0 END) AS CtmFocCount,
        SUM(CASE WHEN IsFOC = 1 AND Device IN ('HHD4') THEN ChargeAmount ELSE 0 END) AS CtmFocAmount,
        SUM(ISNULL(ChargeAmount, 0)) AS TotalChargedAmount
    FROM Classified
    GROUP BY [Date]
),
-- 2. Vehicle Entry Data (From Exited Table)
VehicleData AS
(
    SELECT
        CAST(exit_time AS DATE) AS [Date],
        COUNT(*) AS TotalVehicles
    FROM [AMAAN_PMS].[dbo].[db_tbl_15_car_exited]
    WHERE YEAR(exit_time) = @Year AND MONTH(exit_time) = @Month
    GROUP BY CAST(exit_time AS DATE)
),
-- 3. TOP-UP Transaction Data
TopUpAgg AS (
    SELECT
        CAST([created_on] AS DATE) AS [Date],
        COUNT(CASE WHEN [transaction_type] = 'TOP-UP' AND [payment_source] LIKE '%Cash%' THEN 1 END) AS CashTopUpCount,
        SUM(CASE WHEN [transaction_type] = 'TOP-UP' AND [payment_source] LIKE '%Cash%' THEN ISNULL([paidamount], 0) ELSE 0 END) AS CashTopUpTotal,
        COUNT(CASE WHEN [transaction_type] = 'TOP-UP' AND ([payment_source] LIKE '%Mpesa%' OR [payment_source] LIKE '%Web%') THEN 1 END) AS MPesaTopUpCount,
        SUM(CASE WHEN [transaction_type] = 'TOP-UP' AND ([payment_source] LIKE '%Mpesa%' OR [payment_source] LIKE '%Web%') THEN ISNULL([paidamount], 0) ELSE 0 END) AS MPesaTopUpTotal
    FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions]
    WHERE YEAR([created_on]) = @Year AND MONTH([created_on]) = @Month AND [transaction_type] = 'TOP-UP'
    GROUP BY CAST([created_on] AS DATE)
)
-- Final Selection
SELECT 
    D.[Date],
    ISNULL(V.TotalVehicles, 0) AS TotalVehicles,
    D.TotalTransactions,
    D.CashCount,
    D.CashTotal,
    D.MPesaCount,
    D.MPesaTotal,
    D.FOCCount,
    D.FOCAmount,
    D.CtmFocCount,
    D.CtmFocAmount,
    D.TotalChargedAmount,
    ISNULL(T.CashTopUpCount, 0) AS CashTopUpCount,
    ISNULL(T.CashTopUpTotal, 0) AS CashTopUpTotal,
    ISNULL(T.MPesaTopUpCount, 0) AS MPesaTopUpCount,
    ISNULL(T.MPesaTopUpTotal, 0) AS MPesaTopUpTotal
FROM DailyAgg D
LEFT JOIN VehicleData V ON D.[Date] = V.[Date]
LEFT JOIN TopUpAgg T ON D.[Date] = T.[Date]
ORDER BY D.[Date];";

                var result = await connection.QueryAsync<dynamic>(query, new { Year = year, Month = month });

                return result.Select(r => new
                {
                    date = ((DateTime)r.Date).ToString("yyyy-MM-dd"),
                    totalVehicles = (int)r.TotalVehicles,
                    totalTransactions = (int)r.TotalTransactions,
                    cashCount = (int)r.CashCount,
                    cashTotal = (decimal)r.CashTotal,
                    mPesaCount = (int)r.MPesaCount,
                    mPesaTotal = (decimal)r.MPesaTotal,
                    focCount = (int)r.FOCCount,
                    focAmount = (decimal)r.FOCAmount,
                    ctmFocCount = (int)r.CtmFocCount,
                    ctmFocAmount = (decimal)r.CtmFocAmount,
                    totalChargedAmount = (decimal)r.TotalChargedAmount,
                    // Combined totals for the Transaction tab's "Total Amount" column
                    totalAmount = (decimal)r.CashTotal + (decimal)r.MPesaTotal,
                    // TOP-UP specific fields
                    cashTopUpCount = (int)r.CashTopUpCount,
                    cashTopUpTotal = (decimal)r.CashTopUpTotal,
                    mPesaTopUpCount = (int)r.MPesaTopUpCount,
                    mPesaTopUpTotal = (decimal)r.MPesaTopUpTotal
                }).ToList<dynamic>();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching transaction data: {ex.Message}");
            }
        }

        // ============================================
        // HELPER METHODS - NO CHANGES
        // ============================================
        private string NormalizePaymentSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return "Server";
            string clean = source.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "").Replace(",", "");
            return clean switch
            {
                "SERVER" => "Server",
                "PAYSTATION1" or "PS1" or "PAYSTATION01" => "Paystation1",
                "PAYSTATION2" or "PS2" or "PAYSTATION02" => "Paystation2",
                "PAYSTATION3" or "PS3" or "PAYSTATION03" => "Paystation3",
                "PAYSTATION4" or "PS4" or "PAYSTATION04" => "Paystation4",
                "HHD1" or "HHD01" or "HANDHELD1" => "HHD1",
                "HHD2" or "HHD02" or "HANDHELD2" => "HHD2",
                "HHD3" or "HHD03" or "HANDHELD3" => "HHD3",
                "HHD4" or "HHD04" or "HANDHELD4" => "HHD4",
                "WEB" or "ONLINE" or "WEBSITE" => "Web",
                _ => source.Trim()
            };
        }

        /// <summary>
        /// Normalize PayMode values for consistent calculation
        /// M-pesa + M-Pesa = M-Pesa (direct app)
        /// M-Pesa Via Paybill = Separate
        /// Cash, Free of Charge, auto = As-is
        /// </summary>
        private string NormalizePayMode(string payMode)
        {
            if (string.IsNullOrWhiteSpace(payMode))
                return "";

            string normalized = payMode.Trim().ToUpperInvariant()
                .Replace(" ", "").Replace("-", "").Replace("_", "");

            if (normalized == "MPESA")
                return "M-Pesa";

            if (normalized == "MPESAVIAPAYBILL" || normalized == "MPESAVIAPAYBILL")
                return "M-Pesa Via Paybill";

            if (normalized == "CASH")
                return "Cash";

            // ✅ FIXED: Both "FOC" and "FREE OF CHARGE" (any case) → "Free of Charge"
            if (normalized == "FOC" || normalized == "FREEOFCHARGE")
                return "Free of Charge";

            if (normalized == "AUTO" || normalized == "AUTOMATIC")
                return "Auto";

            return payMode.Trim();
        }

        private List<dynamic> CreateEmptySummary()
        {
            var paymentModes = new[] { "Cash", "M-Pesa", "M-Pesa Via Paybill", "Free of Charge" };
            var summaryRows = new List<dynamic>();

            foreach (var payMode in paymentModes)
            {
                summaryRows.Add(new
                {
                    PayMode = payMode,
                    ServerCount = 0,
                    Server = 0m,
                    Paystation1Count = 0,
                    Paystation1 = 0m,
                    Paystation2Count = 0,
                    Paystation2 = 0m,
                    Paystation3Count = 0,
                    Paystation3 = 0m,
                    Paystation4Count = 0,
                    Paystation4 = 0m,
                    HHD1Count = 0,
                    HHD1 = 0m,
                    HHD2Count = 0,
                    HHD2 = 0m,
                    HHD3Count = 0,
                    HHD3 = 0m,
                    HHD4Count = 0,
                    HHD4 = 0m,
                    WebCount = 0,
                    Web = 0m,
                    Total = 0m
                });
            }

            return summaryRows;
        }

        private dynamic CreateEmptyStatistics()
        {
            string[] devices = { "Server", "Paystation1", "Paystation2", "Paystation3", "Paystation4", "HHD1", "HHD2", "HHD3", "HHD4", "Web" };
            var deviceStats = new Dictionary<string, dynamic>();

            foreach (var device in devices)
            {
                deviceStats[device] = new
                {
                    cashCount = 0,
                    cashTotal = 0m,
                    mPesaCount = 0,
                    mPesaTotal = 0m,
                    mPesaViaPaybillCount = 0,
                    mPesaViaPaybillTotal = 0m,
                    focCount = 0,
                    focTotal = 0m
                };
            }

            return new
            {
                devices = deviceStats,
                totalVehicles = 0,
                totalCharges = 0m,
                totalCashCount = 0,
                totalCashCollected = 0m,
                totalMPesaCount = 0,
                totalMPesaCollected = 0m,
                totalMPesaViaPaybillCount = 0,
                totalMPesaViaPaybillCollected = 0m,
                totalFocCount = 0,
                totalFocAmount = 0m,
                creditTakenCount = 0,
                equityCount = 0,
                advancePaidCount = 0,
                reparkCount = 0,
                grandTotal = 0,
                grandTotalAmount = 0m
            };
        }
    }

    public class PaymentBreakdownItem
    {
        public string Source { get; set; }
        public string PayMode { get; set; }
        public decimal Amount { get; set; }
        public string TransactionType { get; set; }
    }
}
