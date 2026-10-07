namespace AmaanParkingSystem.Models.Collection
{
    // db_tbl_15_car_exited
    public class DailyCollection
    {
        public int id { get; set; }
        public string customer_id { get; set; }
        public string cardno { get; set; }
        public string card_name { get; set; }
        public string card_type { get; set; }
        public string car_plate_no { get; set; }
        public DateTime? entry_time { get; set; }
        public string fee_schedule_id { get; set; }
        public string fee_schedule_name { get; set; }
        public int? park_time_untill_validation { get; set; }
        public int? park_time_additional { get; set; }
        public decimal? addn_chgs { get; set; }
        public DateTime? validation_time { get; set; }
        public DateTime? revalidation_time { get; set; }
        public decimal? revalidation_charges { get; set; }
        public string action_status { get; set; }
        public decimal? charges { get; set; }
        public string charge_status { get; set; }
        public string discount_token_no { get; set; }
        public decimal? discount_amount { get; set; }
        public string reason { get; set; }
        public string remark { get; set; }
        public decimal? balance_to_collect { get; set; }
        public string payment_source { get; set; }
        public string pay_mode { get; set; }
        public decimal? paid_amount { get; set; }
        public string transaction_id { get; set; }
        public string transaction_status { get; set; }
        public bool? isValidated { get; set; }
        public DateTime? exit_time { get; set; }
        public int? park_time_total { get; set; }
        public string created_by { get; set; }
        public DateTime? created_on { get; set; }
        public string updated_by { get; set; }
        public DateTime? updated_on { get; set; }
        public decimal? total_fee { get; set; }
        public int? chargeable_minutes { get; set; }
        public int? free_minutes { get; set; }
        public int? namaz_exempted_minutes { get; set; }
        public decimal? additional_charges { get; set; }
        public string pay_source { get; set; }
    }

    // db_tbl_19_transactions
    public class TransactionData
    {
        public int id { get; set; }
        public string transaction_id { get; set; }
        public string car_plate_no { get; set; }
        public string cardno { get; set; }
        public string cardname { get; set; }
        public string transaction_type { get; set; }
        public decimal? paidamount { get; set; }
        public string operator_name { get; set; }
        public string transaction_status { get; set; }
        public string payment_source { get; set; }
        public bool? status { get; set; }
        public string created_by { get; set; }
        public DateTime? created_on { get; set; }
        public string updated_by { get; set; }
        public DateTime? updated_on { get; set; }
        public DateTime? validation_time { get; set; }
        public decimal? validation_charges { get; set; }
        public string validation_paymode { get; set; }
        public DateTime? revalidation_time { get; set; }
        public decimal? revalidation_charges { get; set; }  // ADD THIS after revalidation_time
        public string revalidation_paymode { get; set; }
        public DateTime? entry_time { get; set; }
    }

    // ✅ UPDATED - Added StartDate and EndDate
    public class CollectionFilterRequest
    {
        public DateTime SelectedDate { get; set; }  // Keep this for backward compatibility
        public DateTime StartDate { get; set; }     // ✅ ADD THIS
        public DateTime EndDate { get; set; }       // ✅ ADD THIS
    }

    public class MonthlyFilterRequest
    {
        public int Year { get; set; }
        public int Month { get; set; }
    }
}
