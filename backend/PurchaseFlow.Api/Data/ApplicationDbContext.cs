using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace PurchaseFlow.Api.Data;

public class ApplicationDbContext
    : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // PurchaseFlow 規則：每個使用者最多一個角色。
        builder.Entity<IdentityUserRole<string>>()
            .HasIndex(userRole => userRole.UserId)
            .IsUnique();
    }
}