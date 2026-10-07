namespace AmaanParkingSystem.Models
{
    public class OperatorModel
    {
        public int id { get; set; }
        public string operator_type { get; set; } = "";
        public string operator_login_id { get; set; } = "";
        public string operator_name { get; set; } = "";
        public string operator_mobile { get; set; } = "";
        public string operator_password { get; set; } = "";
        public string operator_login_status { get; set; } = "1";  // CHANGED: BIT column - "1"/"0" only
        public string operator_status { get; set; } = "1";        // BIT column - "1"/"0" only
        public string created_by { get; set; } = "";
        public DateTime? created_on { get; set; }
        public string updated_by { get; set; } = "";
        public DateTime? updated_on { get; set; }
    }
}
