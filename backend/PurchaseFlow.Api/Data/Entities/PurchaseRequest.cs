using PurchaseFlow.Api.Data;

namespace PurchaseFlow.Api.Data.Entities;

public class PurchaseRequest
{
    public int Id { get; set; }

    public string RequesterId { get; set; } = "";
    public ApplicationUser Requester { get; set; } = null!;

    public string Title { get; set; } = "";
    public string? Purpose { get; set; }

    public string Status { get; set; } = "Draft";

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? SubmittedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<PurchaseRequestItem> Items { get; set; } = [];
}