namespace PurchaseFlow.Api.Contracts.PurchaseRequests;

public sealed class CreatePurchaseRequestRequest
{
    public string Title { get; set; } = "";

    public string? Purpose { get; set; }

    public List<CreatePurchaseRequestItemRequest> Items { get; set; } = [];
}

public sealed class CreatePurchaseRequestItemRequest
{
    public string Name { get; set; } = "";

    public int Quantity { get; set; }

    public decimal EstimatedUnitPrice { get; set; }
}