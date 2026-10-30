namespace PurchaseFlow.Api.Contracts.PurchaseRequests;

public sealed class PurchaseRequestDecisionRequest
{
    public string Decision { get; set; } = "";

    public string? Comment { get; set; }

    public string RowVersion { get; set; } = "";
}
