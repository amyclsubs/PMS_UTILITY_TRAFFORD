namespace AmaanParkingSystem.Models
{
    public class CardRegistrationModel
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
        public string card_type_remark { get; set; } // NEW: To display remark
        public string car_plate_no { get; set; }
        public string car_owner_name { get; set; }
        public string car_owner_contact_no { get; set; }
        public string applicationform { get; set; }
        public string fee_schedule_id { get; set; }
        public string expiry_date { get; set; }
        public string access_rights { get; set; }
        public string antipassback { get; set; }
        public string grouped { get; set; }
        public string group_type { get; set; }
        public string group_id { get; set; }
        public string master_or_sub_card { get; set; }
        public string balance { get; set; }
        public string status { get; set; }
        public string created_by { get; set; }
        public string created_on { get; set; }
        public string updated_by { get; set; }
        public string updated_on { get; set; }
    }
}
