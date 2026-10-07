namespace AmaanParkingSystem.Models.UserManagement
{
    public class ParkerCompanyMasterModel
    {
        public int id { get; set; }
        public string? customer_id { get; set; }
        public string? car_parkers_company_id { get; set; } // Auto-generated CPC_0002
        public string? car_parkers_company_name { get; set; }
        public string? car_parkers_contact_person { get; set; }
        public string? car_parkers_email { get; set; }
        public string? car_parkers_contact_number { get; set; }
        public string? car_parkers_address { get; set; }
        public string? car_parkers_status { get; set; }
        public string? created_by { get; set; }
        public DateTime? created_on { get; set; }
        public string? updated_by { get; set; }
        public DateTime? updated_on { get; set; }
    }
}
