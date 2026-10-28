using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PurchaseFlow.Api.Data;
using PurchaseFlow.Api.Data.Entities;

namespace PurchaseFlow.Api.Tests;

public class PurchaseRequestConcurrencyTests
{
    [Fact]
    public async Task TwoContexts_OnlyFirstUpdateSucceeds()
    {
        using var factory = new SqlServerTestApiFactory();

        string? userId = null;
        int requestId;

        try
        {
            // 建立獨立測試帳號與草稿
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var userManager = scope.ServiceProvider
                    .GetRequiredService<UserManager<ApplicationUser>>();

                var user = new ApplicationUser
                {
                    UserName = $"concurrency-{Guid.NewGuid():N}"
                };

                userId = user.Id;

                var result = await userManager.CreateAsync(
                    user, "TestPassword!4286");

                Assert.True(result.Succeeded);

                var now = DateTimeOffset.UtcNow;

                var draft = new PurchaseRequest
                {
                    RequesterId = userId,
                    Title = "原始草稿",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Items =
                    [
                        new PurchaseRequestItem
                        {
                            Name = "原始品項",
                            Quantity = 1,
                            EstimatedUnitPrice = 100m
                        }
                    ]
                };

                db.PurchaseRequests.Add(draft);
                await db.SaveChangesAsync();

                requestId = draft.Id;
            }

            // 建立兩個完全獨立的 DbContext
            using (var scopeA = factory.Services.CreateScope())
            using (var scopeB = factory.Services.CreateScope())
            {
                var dbA = scopeA.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                var dbB = scopeB.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(dbA);
                EnsureTestDatabase(dbB);

                // A、B 都在任何修改之前讀取同一版本
                var draftA = await dbA.PurchaseRequests
                    .Include(request => request.Items)
                    .SingleAsync(request => request.Id == requestId);

                var draftB = await dbB.PurchaseRequests
                    .Include(request => request.Items)
                    .SingleAsync(request => request.Id == requestId);

                var originalVersion =
                    Convert.ToBase64String(draftA.RowVersion);

                Assert.Equal(
                    originalVersion,
                    Convert.ToBase64String(draftB.RowVersion));

                // A 修改主單與品項
                draftA.Title = "A 修改成功";
                draftA.UpdatedAtUtc = DateTimeOffset.UtcNow;

                dbA.Entry(draftA)
                    .Property(request => request.UpdatedAtUtc)
                    .IsModified = true;

                draftA.Items.Add(new PurchaseRequestItem
                {
                    Name = "A 的品項",
                    Quantity = 2,
                    EstimatedUnitPrice = 500m
                });

                // B 使用仍然保留 V1 的獨立 DbContext
                draftB.Title = "B 不應成功";
                draftB.UpdatedAtUtc = DateTimeOffset.UtcNow;

                dbB.Entry(draftB)
                    .Property(request => request.UpdatedAtUtc)
                    .IsModified = true;

                draftB.Items.Add(new PurchaseRequestItem
                {
                    Name = "B 不應留下的品項",
                    Quantity = 1,
                    EstimatedUnitPrice = 9999m
                });

                // 第一個操作成功
                await dbA.SaveChangesAsync();

                var newVersion =
                    Convert.ToBase64String(draftA.RowVersion);

                Assert.NotEqual(originalVersion, newVersion);

                // 第二個操作使用舊版本，必須失敗
                var exception = await Assert.ThrowsAsync<
                    DbUpdateConcurrencyException>(
                        () => dbB.SaveChangesAsync());

                // 確認衝突涉及主單
                Assert.Contains(
                    exception.Entries,
                    entry => entry.Entity is PurchaseRequest);
            }

            // 使用第三個 DbContext 查詢最終資料
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .SingleAsync(request => request.Id == requestId);

                Assert.Equal("A 修改成功", saved.Title);
                Assert.Equal("Draft", saved.Status);

                Assert.Equal(2, saved.Items.Count);

                Assert.Contains(saved.Items,
                    item => item.Name == "原始品項");

                Assert.Contains(saved.Items,
                    item => item.Name == "A 的品項");

                Assert.DoesNotContain(saved.Items,
                    item => item.Name == "B 不應留下的品項");

                var total = saved.Items.Sum(
                    item => item.Quantity *
                            item.EstimatedUnitPrice);

                Assert.Equal(1100m, total);
            }
        }
        finally
        {
            // 只清理這次測試建立的資料
            if (userId is not null)
            {
                using var scope = factory.Services.CreateScope();

                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var requests = await db.PurchaseRequests
                    .Where(request =>
                        request.RequesterId == userId)
                    .ToListAsync();

                db.PurchaseRequests.RemoveRange(requests);
                await db.SaveChangesAsync();

                var userManager = scope.ServiceProvider
                    .GetRequiredService<UserManager<ApplicationUser>>();

                var user = await userManager.FindByIdAsync(userId);

                if (user is not null)
                {
                    var result = await userManager.DeleteAsync(user);
                    Assert.True(result.Succeeded);
                }
            }
        }
    }

    private static void EnsureTestDatabase(
        ApplicationDbContext db)
    {
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException(
                "Missing test database connection.");

        var settings = new SqlConnectionStringBuilder(
            connectionString);

        Assert.Equal(
            "PurchaseFlowTestsDb",
            settings.InitialCatalog);

        Assert.Equal(
            @".\SQLEXPRESS",
            settings.DataSource);
    }
}
