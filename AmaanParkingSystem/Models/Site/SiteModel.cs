namespace AmaanParkingSystem.Models.Site
{
    public class SiteModel
    {
        public int id { get; set; }
        public string customer_id { get; set; }
        public string site_name { get; set; }
        public string client_name { get; set; }
        public string client_email { get; set; }
        public string client_site_address { get; set; }
        public bool client_status { get; set; }   // BIT
        public string site_id { get; set; }

        // New fields
        public string site_type { get; set; }     // e.g. ADMIN / MALL / HOSPITAL
        public bool receive_daily_exited { get; set; }
        public bool receive_daily_transactions { get; set; }
        public bool receive_monthly_exited { get; set; }
        public bool receive_monthly_transactions { get; set; }

        // Audit (kept for listing)
        public string created_by { get; set; }
        public string created_on { get; set; }
        public string updated_by { get; set; }
        public string updated_on { get; set; }
    }
}
