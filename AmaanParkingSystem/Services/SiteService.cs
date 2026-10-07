using Microsoft.Data.SqlClient;
using Dapper;
using AmaanParkingSystem.Models.Site; // <-- Use the correct namespace!
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AmaanParkingSystem.Services
{
    public class SiteService
    {
        private readonly IConnectionStringService _connStrService;

        public SiteService(IConnectionStringService connStrService)
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

        // Get all sites
        public async Task<List<SiteModel>> GetAllSites()
        {
            try
            {
                using (var connection = GetConnection())
                {
                    var query = @"
                    SELECT 
                        [id], [customer_id], [site_name], [client_name], [client_email],
                        [client_site_address], [client_status], [created_by], [created_on],
                        [updated_by], [updated_on], [site_id]
                    FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                    ORDER BY [created_on] DESC";

                    var result = await connection.QueryAsync<SiteModel>(query);
                    return result.ToList();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching sites: {ex.Message}");
            }
        }

        // Get site by ID
        public async Task<SiteModel> GetSiteById(int id)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    var query = @"
                    SELECT 
                        [id], [customer_id], [site_name], [client_name], [client_email],
                        [client_site_address], [client_status], [created_by], [created_on],
                        [updated_by], [updated_on], [site_id]
                    FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                    WHERE [id] = @Id";

                    var result = await connection.QueryFirstOrDefaultAsync<SiteModel>(query, new { Id = id });
                    return result;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching site: {ex.Message}");
            }
        }

        // Create new site
        public async Task<int> CreateSite(SiteModel site)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    var query = @"
                    INSERT INTO [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                    ([customer_id], [site_name], [client_name], [client_email], [client_site_address],
                     [client_status], [created_by], [created_on], [site_id])
                    VALUES
                    (@customer_id, @site_name, @client_name, @client_email, @client_site_address,
                     @client_status, @created_by, GETDATE(), @site_id);
                    SELECT CAST(SCOPE_IDENTITY() as int)";

                    var result = await connection.ExecuteScalarAsync<int>(query, site);
                    return result;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error creating site: {ex.Message}");
            }
        }

        // Update existing site
        public async Task<bool> UpdateSite(SiteModel site)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    var query = @"
                    UPDATE [AMAAN_PMS].[dbo].[db_tbl_03_site_masters]
                    SET 
                        [customer_id] = @customer_id,
                        [site_name] = @site_name,
                        [client_name] = @client_name,
                        [client_email] = @client_email,
                        [client_site_address] = @client_site_address,
                        [client_status] = @client_status,
                        [updated_by] = @updated_by,
                        [updated_on] = GETDATE(),
                        [site_id] = @site_id
                    WHERE [id] = @id";

                    var rowsAffected = await connection.ExecuteAsync(query, site);
                    return rowsAffected > 0;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error updating site: {ex.Message}");
            }
        }

        // Delete site
        public async Task<bool> DeleteSite(int id)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    var query = "DELETE FROM [AMAAN_PMS].[dbo].[db_tbl_03_site_masters] WHERE [id] = @Id";
                    var rowsAffected = await connection.ExecuteAsync(query, new { Id = id });
                    return rowsAffected > 0;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error deleting site: {ex.Message}");
            }
        }
    }
}
