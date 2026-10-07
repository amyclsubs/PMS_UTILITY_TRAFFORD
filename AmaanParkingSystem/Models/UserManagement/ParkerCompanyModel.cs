namespace AmaanParkingSystem.Models.UserManagement
{
    public class ParkerCompanyModel
    {
        public int id { get; set; }
        public string? customer_id { get; set; }
        public string? car_parkers_company_id { get; set; }
        public string? car_parkers_company_name { get; set; }
        public string? car_parkers_sub_co_id { get; set; }
        public string? car_parkers_sub_co_name { get; set; }
        public string? applicable_card_type { get; set; }
        public string card_type_remark { get; set; }

        public string? sub_co_car_parkers_contact_person { get; set; }
        public string? sub_co_car_parkers_email { get; set; }
        public string? sub_co_car_parkers_contact_number { get; set; }
        public string? sub_co_car_parkers_address { get; set; }
        public string? sub_co_car_parkers_pin { get; set; }
        public string? sub_co_car_parkers_vat { get; set; }
        public string? car_parkers_status { get; set; }
        public string? created_by { get; set; }
        public DateTime? created_on { get; set; }
        public string? updated_by { get; set; }
        public DateTime? updated_on { get; set; }
        public decimal? Total_Paid_Amount { get; set; }
        public decimal? Total_Deducted_Amount { get; set; }
        public string? Total_Cards_Under_Sub_Co { get; set; }
        public string? Total_Card_Limit_Under_Sub_Co { get; set; }
        public string? card_parkers_company_password { get; set; }
        public int? Neg_Balance_allowed { get; set; }  // <-- ADD THIS LINE
        public decimal? Balance_Amount { get; set; }
    }
}
