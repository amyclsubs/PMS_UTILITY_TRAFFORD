using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace AmaanParkingSystem.Services
{
    public class FocReportService
    {
        private readonly IConnectionStringService _connStrService;

        public FocReportService(IConnectionStringService connStrService)
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

        // New: Get distinct payment_source values (devices), still excluding HHD4 variants
        public async Task<List<string>> GetDistinctPaymentSources()
        {
            using var connection = GetConnection();
            var query = @"
        SELECT DISTINCT T.[payment_source]
        FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions] T
        WHERE
            T.[payment_source] IS NOT NULL
            AND LTRIM(RTRIM(T.[payment_source])) <> ''
            AND (
                T.[validation_paymode] = 'Free of Charge' 
                OR UPPER(REPLACE(T.[validation_paymode], ' ', '')) IN ('FOC', 'FREEOFCHARGE')
                OR T.[revalidation_paymode] = 'Free of Charge' 
                OR UPPER(REPLACE(T.[revalidation_paymode], ' ', '')) IN ('FOC', 'FREEOFCHARGE')
            )
        ORDER BY T.[payment_source];";

            var result = await connection.QueryAsync<string>(
    new CommandDefinition(
        query,
        commandTimeout: 120));
            return result.ToList();
        }

        // Updated: optional paymentSources filter but keeps HHD4 exclusion and other logic intact
        public async Task<List<dynamic>> GetFocReportData(
    DateTime startDate,
    DateTime endDate,
    List<string>? paymentSources = null)
        {
            using var connection = GetConnection();

            var query = @"
        SELECT 
            T.[transaction_id], T.[cardname], T.[entry_time], T.[transaction_type], 
            T.[payment_source], T.[validation_time], T.[validation_charges], 
            T.[validation_paymode], T.[revalidation_time], T.[revalidation_charges], 
            T.[revalidation_paymode], E.[reason], E.[remark]
        FROM [AMAAN_PMS].[dbo].[db_tbl_19_transactions] T
        LEFT JOIN [AMAAN_PMS].[dbo].[db_tbl_15_car_exited] E 
            ON T.[cardname] = E.[card_name] 
            AND (
                (E.[validation_time] BETWEEN DATEADD(second, -2, T.[validation_time]) AND DATEADD(second, 2, T.[validation_time]))
                OR 
                (E.[revalidation_time] BETWEEN DATEADD(second, -2, T.[revalidation_time]) AND DATEADD(second, 2, T.[revalidation_time]))
            )
        WHERE 
            (
                T.[validation_paymode] = 'Free of Charge' 
                OR UPPER(REPLACE(T.[validation_paymode], ' ', '')) IN ('FOC', 'FREEOFCHARGE')
                OR T.[revalidation_paymode] = 'Free of Charge' 
                OR UPPER(REPLACE(T.[revalidation_paymode], ' ', '')) IN ('FOC', 'FREEOFCHARGE')
            )
            AND CAST(T.[created_on] AS DATE) BETWEEN @StartDate AND @EndDate";

            if (paymentSources is { Count: > 0 })
            {
                query += @"
            AND T.[payment_source] IN @PaymentSources";
            }

            query += @"
        ORDER BY T.[created_on] DESC;";

            var result = await connection.QueryAsync<dynamic>(
    new CommandDefinition(
        query,
        new {
            StartDate = startDate.Date,
            EndDate = endDate.Date,
            PaymentSources = paymentSources
        },
        commandTimeout: 120));

            return result.ToList();
        }
    }
}
