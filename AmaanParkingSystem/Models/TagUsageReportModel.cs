namespace AmaanParkingSystem.Models.Reports
{
    public class TagUsageReportModel
    {
        // From Cards Allocated Table
        public string cardno { get; set; }
        public string cardname { get; set; }
        public string card_type { get; set; }
        public string card_type_remark { get; set; } // ✅ NEW: To display remark
        public string car_parkers_company_id { get; set; }
        public string car_parkers_sub_co_id { get; set; }
        public string car_parkers_sub_co_name { get; set; }
        public string car_plate_no { get; set; }
        public string car_owner_name { get; set; }
        public string status { get; set; }

        // From Tag Log Table (Last Usage)
        public DateTime? last_entry_time { get; set; }
        public DateTime? last_exit_time { get; set; }
        public DateTime? B1_entry_time { get; set; }
        public DateTime? B1_exit_time { get; set; }
        public DateTime? B2_entry_time { get; set; }
        public DateTime? B2_exit_time { get; set; }
        public decimal? daily_charge { get; set; }
        public decimal? penalty_charge { get; set; }
        public decimal? total_charge { get; set; }
        public string remarks { get; set; }

        // Calculated Fields
        public int? days_since_last_use { get; set; }
        public string usage_status { get; set; }
    }
}
