using PurchaseFlow.Api.Data;

namespace PurchaseFlow.Api.Data.Entities;

public class ApprovalLog
{
    public int Id { get; set; }

    public int PurchaseRequestId { get; set; }
    public PurchaseRequest PurchaseRequest { get; set; } = null!;

    public string ReviewerId { get; set; } = "";
    public ApplicationUser Reviewer { get; set; } = null!;

    public string Decision { get; set; } = "";
    public string? Comment { get; set; }

    public DateTimeOffset ReviewedAtUtc { get; set; }
}
