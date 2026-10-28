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

public class PurchaseRequestAuthorizationTests
{
    [Fact]
    public async Task Manager_CannotCreateOrUpdateDraft_Returns403()
    {
        using var factory = new SqlServerTestApiFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        var userName = $"manager-test-{Guid.NewGuid():N}";
        const string password = "TestPassword!4286";
        string? userId = null;

        try
        {
            // 在獨立測試資料庫建立 Manager
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var roleManager = scope.ServiceProvider
                    .GetRequiredService<RoleManager<IdentityRole>>();

                var userManager = scope.ServiceProvider
                    .GetRequiredService<UserManager<ApplicationUser>>();

                if (!await roleManager.RoleExistsAsync("Manager"))
                {
                    var roleResult = await roleManager.CreateAsync(
                        new IdentityRole("Manager"));

                    Assert.True(roleResult.Succeeded);
                }

                var user = new ApplicationUser
                {
                    UserName = userName
                };

                var createResult = await userManager.CreateAsync(
                    user, password);

                Assert.True(createResult.Succeeded);
                userId = user.Id;

                var roleResult2 = await userManager.AddToRoleAsync(
                    user, "Manager");

                Assert.True(roleResult2.Succeeded);
            }

            // 取得登入前 CSRF Token
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
                    title = "主管越權測試",
                    items = Array.Empty<object>()
                })
            };

            createRequest.Headers.Add("X-CSRF-TOKEN", createCsrf);

            using var response = await client.SendAsync(createRequest);

            // Manager 不應有建立草稿的權限
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);


            // Manager 嘗試修改採購草稿
            using var putRequest = new HttpRequestMessage(
                HttpMethod.Put, "/api/purchase-requests/999999")
            {
                Content = JsonContent.Create(new
                {
                    title = "主管不應能修改草稿",
                    rowVersion = Convert.ToBase64String(new byte[8]),
                    items = Array.Empty<object>()
                })
            };

            // 使用登入後的有效 CSRF Token
            putRequest.Headers.Add("X-CSRF-TOKEN", createCsrf);

            using var putResponse =
                await client.SendAsync(putRequest);

            Assert.Equal(
                HttpStatusCode.Forbidden,
                putResponse.StatusCode);


            // 驗證沒有新增採購申請
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                Assert.False(await db.PurchaseRequests
                    .AnyAsync(request => request.RequesterId == userId));
            }
        }
        finally
        {
            if (userId is not null)
            {
                using var scope = factory.Services.CreateScope();

                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                // 即使測試意外寫入，也清理該使用者的資料
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
                    var deleteResult = await userManager.DeleteAsync(user);
                    Assert.True(deleteResult.Succeeded);
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

        var settings = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal("PurchaseFlowTestsDb", settings.InitialCatalog);
        Assert.Equal(@".\SQLEXPRESS", settings.DataSource);
    }
}
