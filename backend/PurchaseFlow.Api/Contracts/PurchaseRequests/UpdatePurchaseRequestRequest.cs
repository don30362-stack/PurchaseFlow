namespace PurchaseFlow.Api.Contracts.PurchaseRequests;

public sealed class UpdatePurchaseRequestRequest
{
    public string Title { get; set; } = "";

    public string? Purpose { get; set; }

    public List<CreatePurchaseRequestItemRequest> Items { get; set; } = [];

    public string RowVersion { get; set; } = "";
}
