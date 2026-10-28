
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PurchaseFlow.Api.Data;

namespace PurchaseFlow.Api.Tests;

public class PurchaseRequestCreateTests
{
    [Fact]
    public async Task Employee_CanCreateDraft_AndSaveItems()
    {
        using var factory = new SqlServerTestApiFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        var userName = $"test-{Guid.NewGuid():N}";
        const string password = "TestPassword!4286";
        string? userId = null;

        try
        {
            // 建立專屬測試帳號
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var roleManager = scope.ServiceProvider
                    .GetRequiredService<RoleManager<IdentityRole>>();

                var userManager = scope.ServiceProvider
                    .GetRequiredService<UserManager<ApplicationUser>>();

                if (!await roleManager.RoleExistsAsync("Employee"))
                {
                    var roleResult = await roleManager.CreateAsync(
                        new IdentityRole("Employee"));

                    Assert.True(roleResult.Succeeded);
                }

                var user = new ApplicationUser
                {
                    UserName = userName
                };

                userId = user.Id;

                var createUserResult = await userManager.CreateAsync(
                    user, password);

                Assert.True(createUserResult.Succeeded);

                var addRoleResult = await userManager.AddToRoleAsync(
                    user, "Employee");

                Assert.True(addRoleResult.Succeeded);
            }

            // 登入前取得 CSRF Token
            var loginCsrf = await GetCsrfTokenAsync(client);

            using var loginRequest = new HttpRequestMessage(
                HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new
                {
                    userName,
                    password
                })
            };

            loginRequest.Headers.Add("X-CSRF-TOKEN", loginCsrf);

            using var loginResponse =
                await client.SendAsync(loginRequest);

            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

            // 登入後重新取得 CSRF Token
            var createCsrf = await GetCsrfTokenAsync(client);

            using var createRequest = new HttpRequestMessage(
                HttpMethod.Post, "/api/purchase-requests")
            {
                Content = JsonContent.Create(new
                {
                    title = "自動化測試採購",
                    purpose = "測試資料庫寫入",
                    items = new[]
                    {
                        new
                        {
                            name = "鍵盤",
                            quantity = 2,
                            estimatedUnitPrice = 1300m
                        },
                        new
                        {
                            name = "滑鼠",
                            quantity = 3,
                            estimatedUnitPrice = 200m
                        }
                    }
                })
            };

            createRequest.Headers.Add("X-CSRF-TOKEN", createCsrf);

            using var createResponse =
                await client.SendAsync(createRequest);

            Assert.Equal(
                HttpStatusCode.Created,
                createResponse.StatusCode);

            var json = await createResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            var requestId = json.GetProperty("id").GetInt32();

            Assert.Equal(
                "Draft",
                json.GetProperty("status").GetString());

            Assert.Equal(
                3200m,
                json.GetProperty("totalAmount").GetDecimal());

            Assert.Equal(
                2,
                json.GetProperty("items").GetArrayLength());

            var rowVersion = json.GetProperty("rowVersion")
                .GetString();

            Assert.False(string.IsNullOrWhiteSpace(rowVersion));
            Assert.Equal(8, Convert.FromBase64String(rowVersion).Length);

            // 直接從 SQL Server 查詢資料
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .SingleAsync(request => request.Id == requestId);

                Assert.Equal(userId, saved.RequesterId);
                Assert.Equal("Draft", saved.Status);
                Assert.Equal(2, saved.Items.Count);

                var total = saved.Items.Sum(
                    item => item.Quantity * item.EstimatedUnitPrice);

                Assert.Equal(3200m, total);
            }

            // 已登入 Employee，但不提供 CSRF Token
            const string rejectedTitle = "CSRF 自動化拒絕測試";

            using var noCsrfRequest = new HttpRequestMessage(
                HttpMethod.Post, "/api/purchase-requests")
            {
                Content = JsonContent.Create(new
                {
                    title = rejectedTitle,
                    items = Array.Empty<object>()
                })
            };

            // 刻意不加入 X-CSRF-TOKEN Header
            using var noCsrfResponse =
                await client.SendAsync(noCsrfRequest);

            Assert.Equal(
                HttpStatusCode.BadRequest,
                noCsrfResponse.StatusCode);

            // 確認非法請求沒有寫入資料庫
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var inserted = await db.PurchaseRequests.AnyAsync(
                    request => request.RequesterId == userId &&
                               request.Title == rejectedTitle);

                Assert.False(inserted);
            }


            // 測試四種非法輸入
            var invalidCases = new (string Name, object Body)[]
            {
                (
                    "空白標題",
                    new
                    {
                        title = "   ",
                        items = Array.Empty<object>()
                    }
                ),
                (
                    "品項數量為 0",
                    new
                    {
                        title = "數量驗證測試",
                        items = new[]
                        {
                            new
                            {
                                name = "螢幕",
                                quantity = 0,
                                estimatedUnitPrice = 5000m
                            }
                        }
                    }
                ),
                (
                    "單價為負數",
                    new
                    {
                        title = "單價驗證測試",
                        items = new[]
                        {
                            new
                            {
                                name = "鍵盤",
                                quantity = 1,
                                estimatedUnitPrice = -100m
                            }
                        }
                    }
                ),
                (
                    "單價超過兩位小數",
                    new
                    {
                        title = "小數位數驗證測試",
                        items = new[]
                        {
                            new
                            {
                                name = "滑鼠",
                                quantity = 1,
                                estimatedUnitPrice = 99.999m
                            }
                        }
                    }
                )
            };

            foreach (var testCase in invalidCases)
            {
                using var invalidRequest = new HttpRequestMessage(
                    HttpMethod.Post, "/api/purchase-requests")
                {
                    Content = JsonContent.Create(testCase.Body)
                };

                // 使用登入後的有效 CSRF Token
                invalidRequest.Headers.Add(
                    "X-CSRF-TOKEN", createCsrf);

                using var invalidResponse =
                    await client.SendAsync(invalidRequest);

                Assert.True(
                    invalidResponse.StatusCode == HttpStatusCode.BadRequest,
                    $"{testCase.Name}：預期 400，實際為 {(int)invalidResponse.StatusCode}");
            }

            // 確認非法輸入沒有增加資料
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var requestCount = await db.PurchaseRequests.CountAsync(
                    request => request.RequesterId == userId);

                // 本測試只應保留前面成功建立的那一張草稿
                Assert.Equal(1, requestCount);
            }

        }
        finally
        {
            // 清理本次測試產生的帳號與申請
            if (userId is not null)
            {
                using var scope = factory.Services.CreateScope();

                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var requests = await db.PurchaseRequests
                    .Where(request => request.RequesterId == userId)
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

    private static async Task<string> GetCsrfTokenAsync(
        HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>(
            "/api/auth/csrf");

        return json.GetProperty("csrfToken").GetString()!;
    }

    private static void EnsureTestDatabase(
        ApplicationDbContext db)
    {
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException(
                "Missing test database connection.");

        var settings = new SqlConnectionStringBuilder(
            connectionString);

        // 防止整合測試誤用開發資料庫
        Assert.Equal(
            "PurchaseFlowTestsDb",
            settings.InitialCatalog);

        Assert.Equal(
            @".\SQLEXPRESS",
            settings.DataSource);
    }
}
