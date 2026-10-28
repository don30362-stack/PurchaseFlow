using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PurchaseFlow.Api.Data;

namespace PurchaseFlow.Api.Tests;

public class SqlServerConnectionTests
{
    [Fact]
    public async Task TestDatabase_IsAvailableAndUpToDate()
    {
        using var factory = new SqlServerTestApiFactory();
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        var connectionString = db.Database.GetConnectionString();

        Assert.NotNull(connectionString);

        var settings = new SqlConnectionStringBuilder(
            connectionString);

        // 連線前先確認不是原本的開發資料庫。
        Assert.Equal(
            "PurchaseFlowTestsDb",
            settings.InitialCatalog);

        // 確認 SQL Server 可連線。
        Assert.True(await db.Database.CanConnectAsync());

        // 確認所有 Migration 都已套用。
        var pendingMigrations =
            await db.Database.GetPendingMigrationsAsync();

        Assert.Empty(pendingMigrations);
    }
}