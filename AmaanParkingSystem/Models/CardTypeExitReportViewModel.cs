public class CardTypeExitReportViewModel
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<CardTypeExitSummary> ReportData { get; set; } = new();
}

public class CardTypeExitSummary
{
    public string CardType { get; set; }
    public int TotalExits { get; set; }
    public decimal TotalCharges { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal TotalPending { get; set; }
    public int PaidCount { get; set; }
    public int UnpaidCount { get; set; }
}
