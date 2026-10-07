namespace AmaanParkingSystem.Models.UserManagement
{
    // ✅ NEW: Top-level Site Model
    public class SiteHierarchyModel
    {
        public int id { get; set; }
        public string customer_id { get; set; }
        public string site_name { get; set; }
        public string client_name { get; set; }
        public string client_email { get; set; }
        public string client_site_address { get; set; }
        public int client_status { get; set; }

        // Master Companies under this Site
        public List<MasterCompanyModel> MasterCompanies { get; set; } = new List<MasterCompanyModel>();
    }
    public class MasterCompanyModel
    {
        public int id { get; set; }
        public string customer_id { get; set; }
        public string car_parkers_company_id { get; set; }
        public string car_parkers_company_name { get; set; }
        public string car_parkers_contact_person { get; set; }
        public string car_parkers_email { get; set; }
        public string car_parkers_contact_number { get; set; }
        public string car_parkers_address { get; set; }
        public string car_parkers_status { get; set; }
        public string created_by { get; set; }
        public DateTime? created_on { get; set; }
        public string updated_by { get; set; }
        public DateTime? updated_on { get; set; }

        // ADD THIS for hierarchy! Fetched from SiteMaster
        public string Site_Name { get; set; }

        // Subcompanies under this master
        public List<SubCompanyModel> SubCompanies { get; set; } = new List<SubCompanyModel>();
    }

    public class SubCompanyModel
    {
        public int id { get; set; }
        public string customer_id { get; set; }
        public string car_parkers_company_id { get; set; }
        public string car_parkers_company_name { get; set; }
        public string car_parkers_sub_co_id { get; set; }
        public string car_parkers_sub_co_name { get; set; }
        public string applicable_card_type { get; set; }
        public string sub_co_car_parkers_contact_person { get; set; }
        public string sub_co_car_parkers_email { get; set; }
        public string sub_co_car_parkers_contact_number { get; set; }
        public string sub_co_car_parkers_address { get; set; }
        public string sub_co_car_parkers_pin { get; set; }
        public string sub_co_car_parkers_vat { get; set; }
        public string car_parkers_status { get; set; }
        public string created_by { get; set; }
        public DateTime? created_on { get; set; }
        public string updated_by { get; set; }
        public DateTime? updated_on { get; set; }

        // Numbers, limits, balances
        public decimal? Total_Paid_Amount { get; set; }
        public decimal? Total_Deducted_Amount { get; set; }
        public string? Total_Cards_Under_Sub_Co { get; set; }
        public string? Total_Card_Limit_Under_Sub_Co { get; set; }
        public decimal? Balance_Amount { get; set; }

        // Password column displayed in UI and exported
        public string card_parkers_company_password { get; set; }

        // Card list
        public List<AllocatedCardModel> Cards { get; set; } = new List<AllocatedCardModel>();
    }

    public class AllocatedCardModel
    {
        public int id { get; set; }
        public string customer_id { get; set; }
        public string car_parkers_company_id { get; set; }
        public string car_parkers_company_name { get; set; }
        public string car_parkers_sub_co_id { get; set; }
        public string car_parkers_sub_co_name { get; set; }
        public string cardno { get; set; }
        public string cardname { get; set; }
        public string card_type { get; set; }
        public string card_type_remark { get; set; } // NEW: To hold the remark value
        public string car_plate_no { get; set; }
        public string car_owner_name { get; set; }
        public string car_owner_contact_no { get; set; }
        public string applicationform { get; set; }
        public string fee_schedule_id { get; set; }
        public DateTime? expiry_date { get; set; }
        public string access_rights { get; set; }
        public string antipassback { get; set; }
        public string grouped { get; set; }
        public string group_type { get; set; }
        public string group_id { get; set; }
        public string master_or_sub_card { get; set; }
        public decimal? balance { get; set; }
        public string status { get; set; }
        public string created_by { get; set; }
        public DateTime? created_on { get; set; }
        public string updated_by { get; set; }
        public DateTime? updated_on { get; set; }
    }

    public class SiteMasterModel
    {
        public int id { get; set; }
        public string customer_id { get; set; }
        public string site_name { get; set; }
        public string client_name { get; set; }
        public string client_email { get; set; }
        public string client_site_address { get; set; }
        public string client_status { get; set; }
        public string site_id { get; set; }
    }
    public class CardMasterModel
    {
        public string card_type { get; set; }
        public string Remark { get; set; }
        public string Sub_Company_Shortcode { get; set; }
    }

}
