namespace AmaanParkingSystem.Models
{
    public class EmailConfig
    {
        public int id { get; set; }
        public string config_name { get; set; }
        public string smtp_server { get; set; }
        public int smtp_port { get; set; }
        public string smtp_username { get; set; }
        public string smtp_password { get; set; }
        public string from_email { get; set; }
        public string from_name { get; set; }
        public bool enable_ssl { get; set; }
        public bool is_active { get; set; }
        public DateTime created_on { get; set; }
        public DateTime? updated_on { get; set; }
    }

    public class EmailRecipient
    {
        public int id { get; set; }
        public string recipient_name { get; set; }
        public string recipient_email { get; set; }
        public string recipient_type { get; set; }
        public bool is_active { get; set; }
        public DateTime created_on { get; set; }
        public DateTime? updated_on { get; set; }
    }
    public class CompanyEmailRequest
    {
        public string companyId { get; set; }
        public string email { get; set; }
    }
}
