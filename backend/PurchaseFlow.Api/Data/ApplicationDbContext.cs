using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PurchaseFlow.Api.Data.Entities;

namespace PurchaseFlow.Api.Data;

public class ApplicationDbContext
    : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<PurchaseRequest> PurchaseRequests =>
        Set<PurchaseRequest>();

    public DbSet<PurchaseRequestItem> PurchaseRequestItems =>
        Set<PurchaseRequestItem>();

    public DbSet<ApprovalLog> ApprovalLogs =>
        Set<ApprovalLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // PurchaseFlow 規則：每個使用者最多一個角色。
        builder.Entity<IdentityUserRole<string>>()
            .HasIndex(userRole => userRole.UserId)
            .IsUnique();

        // 採購申請主表
        builder.Entity<PurchaseRequest>(entity =>
        {
            entity.ToTable("PurchaseRequests", table =>
            {
                table.HasCheckConstraint(
                    "CK_PurchaseRequests_Status",
                    "[Status] in (N'Draft', N'Pending', N'Approved', N'Rejected')"
                );
            });

            entity.HasKey(request => request.Id);

            entity.Property(request => request.Title)
                .HasMaxLength(120)
                .IsRequired();

            entity.Property(request => request.Purpose)
                .HasMaxLength(1000);

            entity.Property(request => request.Status)
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(request => request.RowVersion)
                .IsRowVersion();

            entity.HasOne(request => request.Requester)
                .WithMany()
                .HasForeignKey(request => request.RequesterId)
                .OnDelete(DeleteBehavior.NoAction)
                .IsRequired();

            entity.HasMany(request => request.Items)
                .WithOne(item => item.PurchaseRequest)
                .HasForeignKey(item => item.PurchaseRequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 採購品項明細表
        builder.Entity<PurchaseRequestItem>(entity =>
        {
            entity.ToTable("PurchaseRequestItems", table =>
            {
                table.HasCheckConstraint(
                    "CK_PurchaseRequestItems_Quantity",
                    "[Quantity] BETWEEN 1 AND 999");

                table.HasCheckConstraint(
                    "CK_PurchaseRequestItems_EstimatedUnitPrice",
                    "[EstimatedUnitPrice] > 0");
            });

            entity.HasKey(item => item.Id);

            entity.Property(item => item.Name)
                .HasMaxLength(150)
                .IsRequired();

            entity.Property(item => item.EstimatedUnitPrice)
                .HasPrecision(18, 2);
        });

        // 主管審核紀錄表
        builder.Entity<ApprovalLog>(entity =>
        {
            entity.ToTable("ApprovalLogs", table =>
            {
                table.HasCheckConstraint(
                    "CK_ApprovalLogs_Decision",
                    "[Decision] IN (N'Approved', N'Rejected')");
            });

            entity.HasKey(log => log.Id);

            entity.Property(log => log.Decision)
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(log => log.Comment)
                .HasMaxLength(500);

            // 每張申請最多只能有一筆最終審核紀錄
            entity.HasIndex(log => log.PurchaseRequestId)
                .IsUnique();

            entity.HasOne(log => log.PurchaseRequest)
                .WithOne(request => request.ApprovalLog)
                .HasForeignKey<ApprovalLog>(
                    log => log.PurchaseRequestId)
                .OnDelete(DeleteBehavior.NoAction)
                .IsRequired();

            // 審核紀錄必須保留審核主管的身份
            entity.HasOne(log => log.Reviewer)
                .WithMany()
                .HasForeignKey(log => log.ReviewerId)
                .OnDelete(DeleteBehavior.NoAction)
                .IsRequired();
        });

    }
}