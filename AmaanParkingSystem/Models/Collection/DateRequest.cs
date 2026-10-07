using System;

namespace AmaanParkingSystem.Models.Collection
{
    // ✅ UPDATED - Added StartDate and EndDate
    public class DateRequest
    {
        public DateTime SelectedDate { get; set; }  // Keep this for backward compatibility
        public DateTime StartDate { get; set; }     // ✅ ADD THIS
        public DateTime EndDate { get; set; }       // ✅ ADD THIS
    }
}
