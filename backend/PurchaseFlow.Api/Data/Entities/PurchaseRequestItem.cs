namespace PurchaseFlow.Api.Data.Entities;

public class PurchaseRequestItem
{
    public int Id { get; set; }

    public int PurchaseRequestId { get; set; }
    public PurchaseRequest PurchaseRequest { get; set; } = null!;

    public string Name { get; set; } = "";

    public int Quantity { get; set; }

    public decimal EstimatedUnitPrice { get; set; }
}