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
    }
}