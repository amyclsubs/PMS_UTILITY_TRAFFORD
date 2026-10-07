namespace AmaanParkingSystem.Models.ANPR
{
    public class ANPRRow
    {
        public int Id { get; set; }                 // anpr_records.id
        public string Date { get; set; }            // dd/MM/yyyy
        public string Time { get; set; }            // HH:mm:ss
        public string Camera { get; set; }          // Device
        public string PlateNumber { get; set; }     // Modifiedtext / Extractedtext
        public decimal Confidence { get; set; }     // 0 for now (table lacks confidence)
        public string VehicleImage { get; set; }    // Largeimage
        public string PlateImage { get; set; }      // Coreppedimage
    }
}
