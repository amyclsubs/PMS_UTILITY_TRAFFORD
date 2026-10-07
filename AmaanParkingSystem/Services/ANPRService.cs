using AmaanParkingSystem.Models.ANPR;
using Dapper;
using Microsoft.Data.SqlClient;
using System.Data;

namespace AmaanParkingSystem.Services
{
    public class ANPRService
    {
        private readonly IConnectionStringService _connStrService;
        public ANPRService(IConnectionStringService connStrService) { _connStrService = connStrService; }

        private SqlConnection GetConnection()
        {
            // ANPRService uses the same dynamic connection; override InitialCatalog to ANPR database.
            var baseCs = _connStrService.GetConnectionString();
            if (string.IsNullOrEmpty(baseCs))
                throw new InvalidOperationException("Database connection string is not configured.");
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(baseCs)
            {
                InitialCatalog = "ANPR"
            };
            return new SqlConnection(builder.ConnectionString);
        }

        private const string TABLE = "[ANPR].[dbo].[anpr_records]";

        // Build DateTime from Date + TIme regardless of NVARCHAR/DATE/TIME storage
        private const string DateTimeExpr = @"
            TRY_CONVERT(datetime, CONCAT(
                CASE WHEN ISDATE([Date])=1 THEN CONVERT(varchar(10), [Date], 120) ELSE [Date] END, ' ',
                CASE WHEN ISDATE([TIme])=1 THEN CONVERT(varchar(8),  [TIme], 108) ELSE [TIme] END
            ))";

        private const string BaseFilter = $"FROM {TABLE} WHERE {DateTimeExpr} >= @From AND {DateTimeExpr} < @To";

        public async Task<(ANPRStats stats, List<ANPRRow> rows)> GetReport(DateTime fromInclusive, DateTime toExclusive)
        {
            using var con = GetConnection();
            await con.OpenAsync();

            var total = await con.ExecuteScalarAsync<int>($"SELECT COUNT(*) {BaseFilter}", new { From = fromInclusive, To = toExclusive });

            var unique = await con.ExecuteScalarAsync<int>($@"
                SELECT COUNT(DISTINCT COALESCE(NULLIF(LTRIM(RTRIM(Modifiedtext)),''), NULLIF(LTRIM(RTRIM(Extractedtext)),'')))
                {BaseFilter}", new { From = fromInclusive, To = toExclusive });

            decimal avg = 0m; // confidence not stored in table

            var rows = (await con.QueryAsync<ANPRRow>($@"
                SELECT TOP 500
                    id AS Id,
                    CASE WHEN ISDATE([Date])=1 THEN CONVERT(varchar(10), [Date], 103) ELSE [Date] END AS [Date],
                    CASE WHEN ISDATE([TIme])=1 THEN CONVERT(varchar(8),  [TIme], 108) ELSE [TIme] END AS [Time],
                    Device AS Camera,
                    COALESCE(NULLIF(LTRIM(RTRIM(Modifiedtext)),''), NULLIF(LTRIM(RTRIM(Extractedtext)),'')) AS PlateNumber,
                    CAST(0 AS decimal(5,2)) AS Confidence,
                    Largeimage AS VehicleImage,
                    Coreppedimage AS PlateImage
                {BaseFilter}
                ORDER BY {DateTimeExpr} DESC;",
                new { From = fromInclusive, To = toExclusive })).ToList();

            var stats = new ANPRStats
            {
                TotalRecords = total,
                UniquePlates = unique,
                AverageConfidence = avg,
                DateRange = $"{fromInclusive:dd/MM/yyyy} - {(toExclusive.AddDays(-1)):dd/MM/yyyy}"
            };

            return (stats, rows);
        }

        public Task<(ANPRStats stats, List<ANPRRow> rows)> GetToday() =>
            GetReport(DateTime.Today, DateTime.Today.AddDays(1));

        public Task<(ANPRStats stats, List<ANPRRow> rows)> GetYesterday() =>
            GetReport(DateTime.Today.AddDays(-1), DateTime.Today);

        public Task<(ANPRStats stats, List<ANPRRow> rows)> GetThisWeek()
        {
            var today = DateTime.Today;
            var from = today.AddDays(-(int)today.DayOfWeek); // Sunday start; adjust if Monday
            var to = from.AddDays(7);
            return GetReport(from, to);
        }

        public Task<(ANPRStats stats, List<ANPRRow> rows)> GetThisMonth()
        {
            var today = DateTime.Today;
            var from = new DateTime(today.Year, today.Month, 1);
            var to = from.AddMonths(1);
            return GetReport(from, to);
        }

        public async Task<DataTable> ExportToDataTable(DateTime startInclusive, DateTime endExclusive)
        {
            using var con = GetConnection();
            var dt = new DataTable();
            using var cmd = new SqlCommand($@"
                SELECT 
                    id                AS [ID],
                    CASE WHEN ISDATE([Date])=1 THEN CONVERT(varchar(10), [Date], 103) ELSE [Date] END AS [Date],
                    CASE WHEN ISDATE([TIme])=1 THEN CONVERT(varchar(8),  [TIme], 108) ELSE [TIme] END AS [Time],
                    Device            AS [Camera],
                    COALESCE(NULLIF(LTRIM(RTRIM(Modifiedtext)),''), NULLIF(LTRIM(RTRIM(Extractedtext)),'')) AS [Plate Number],
                    Largeimage        AS [Vehicle Image],
                    Coreppedimage     AS [Plate Image]
                {BaseFilter}
                ORDER BY {DateTimeExpr} DESC;", con);
            cmd.Parameters.AddWithValue("@From", startInclusive);
            cmd.Parameters.AddWithValue("@To", endExclusive);
            using var adp = new SqlDataAdapter(cmd);
            adp.Fill(dt);
            return dt;
        }
    }
}
