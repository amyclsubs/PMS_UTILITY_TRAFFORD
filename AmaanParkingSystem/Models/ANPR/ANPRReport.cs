using System;

namespace AmaanParkingSystem.Models.ANPR
{
    public class ANPRReport
    {
        public int ReportId { get; set; }          // optional: for future detailed report rows
        public string PlateNumber { get; set; }
        public string CameraName { get; set; }
        public DateTime CaptureDateTime { get; set; }
        public decimal ConfidenceScore { get; set; }
        public string VehicleImagePath { get; set; }
        public string PlateImagePath { get; set; }
    }

    public class ANPRStats
    {
        public int TotalRecords { get; set; }
        public int UniquePlates { get; set; }
        public decimal AverageConfidence { get; set; }
        public string DateRange { get; set; }
    }

    public class ANPRFilterRequest
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
    }
}
