using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PurchaseFlow.Api.Data;
using PurchaseFlow.Api.Data.Entities;

namespace PurchaseFlow.Api.Tests;

public class PurchaseRequestOwnershipTests
{
    [Fact]
    public async Task Employee_CanOnlyReadOwnRequests()
    {
        using var factory = new SqlServerTestApiFactory();

        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        var ownerName = $"owner-{Guid.NewGuid():N}";
        var otherName = $"other-{Guid.NewGuid():N}";

        const string password = "TestPassword!4286";

        var createdUserIds = new List<string>();
        var ownerRequestId = 0;
        var otherRequestId = 0;
        string otherVersion;

        try
        {
            // 準備兩個 Employee 和兩張草稿
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var userManager = scope.ServiceProvider
                    .GetRequiredService<UserManager<ApplicationUser>>();

                var roleManager = scope.ServiceProvider
                    .GetRequiredService<RoleManager<IdentityRole>>();

                if (!await roleManager.RoleExistsAsync("Employee"))
                {
                    var result = await roleManager.CreateAsync(
                        new IdentityRole("Employee"));

                    Assert.True(result.Succeeded);
                }

                var owner = new ApplicationUser
                {
                    UserName = ownerName
                };

                var other = new ApplicationUser
                {
                    UserName = otherName
                };

                foreach (var user in new[] { owner, other })
                {
                    var result = await userManager.CreateAsync(
                        user, password);

                    Assert.True(result.Succeeded);

                    createdUserIds.Add(user.Id);

                    result = await userManager.AddToRoleAsync(
                        user, "Employee");

                    Assert.True(result.Succeeded);
                }

                var now = DateTimeOffset.UtcNow;

                var ownerRequest = new PurchaseRequest
                {
                    RequesterId = owner.Id,
                    Title = "Employee A 的申請",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Items = []
                };

                var otherRequest = new PurchaseRequest
                {
                    RequesterId = other.Id,
                    Title = "Employee B 的申請",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Items = []
                };

                db.PurchaseRequests.AddRange(
                    ownerRequest, otherRequest);

                await db.SaveChangesAsync();

                ownerRequestId = ownerRequest.Id;
                otherRequestId = otherRequest.Id;
                otherVersion = Convert.ToBase64String(
                    otherRequest.RowVersion);
            }

            // Employee A 登入
            var loginCsrf = await GetCsrfTokenAsync(client);

            using var loginRequest = new HttpRequestMessage(
                HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new
                {
                    userName = ownerName,
                    password
                })
            };

            loginRequest.Headers.Add(
                "X-CSRF-TOKEN", loginCsrf);

            using var loginResponse =
                await client.SendAsync(loginRequest);

            Assert.Equal(
                HttpStatusCode.OK,
                loginResponse.StatusCode);

            // 查詢清單：只能看見 A 的申請
            using var listResponse = await client.GetAsync(
                "/api/purchase-requests?view=mine");

            Assert.Equal(
                HttpStatusCode.OK,
                listResponse.StatusCode);

            var listJson = await listResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            Assert.Equal(
                JsonValueKind.Array,
                listJson.ValueKind);

            Assert.Equal(1, listJson.GetArrayLength());

            Assert.Equal(
                ownerRequestId,
                listJson[0].GetProperty("id").GetInt32());

            // 查詢自己的明細：200
            using var ownResponse = await client.GetAsync(
                $"/api/purchase-requests/{ownerRequestId}");

            Assert.Equal(
                HttpStatusCode.OK,
                ownResponse.StatusCode);

            // 查詢 B 的明細：404
            using var otherResponse = await client.GetAsync(
                $"/api/purchase-requests/{otherRequestId}");

            Assert.Equal(
                HttpStatusCode.NotFound,
                otherResponse.StatusCode);

            // Employee A 嘗試修改 Employee B 的草稿
            var updateCsrf = await GetCsrfTokenAsync(client);

            using var putRequest = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/purchase-requests/{otherRequestId}")
            {
                Content = JsonContent.Create(new
                {
                    title = "Employee A 嘗試修改 B 的草稿",
                    purpose = "不應成功",
                    rowVersion = otherVersion,
                    items = Array.Empty<object>()
                })
            };

            putRequest.Headers.Add(
                "X-CSRF-TOKEN", updateCsrf);

            using var putResponse =
                await client.SendAsync(putRequest);

            Assert.Equal(
                HttpStatusCode.NotFound,
                putResponse.StatusCode);

            // 確認 B 的草稿完全沒有被修改
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .SingleAsync(request =>
                        request.Id == otherRequestId);

                Assert.Equal(
                    "Employee B 的申請",
                    saved.Title);

                Assert.Equal("Draft", saved.Status);

                Assert.Equal(
                    otherVersion,
                    Convert.ToBase64String(saved.RowVersion));

                Assert.Empty(saved.Items);
            }

            // Employee A 嘗試送審 Employee B 的草稿
            var submitCsrf = await GetCsrfTokenAsync(client);

            using var submitRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/purchase-requests/{otherRequestId}/submit")
            {
                Content = JsonContent.Create(new
                {
                    rowVersion = otherVersion
                })
            };

            submitRequest.Headers.Add(
                "X-CSRF-TOKEN", submitCsrf);

            using var submitResponse =
                await client.SendAsync(submitRequest);

            Assert.Equal(
                HttpStatusCode.NotFound,
                submitResponse.StatusCode);

            // 確認 Employee B 的申請未受到影響
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .SingleAsync(request =>
                        request.Id == otherRequestId);

                Assert.Equal(
                    "Employee B 的申請",
                    saved.Title);

                Assert.Equal("Draft", saved.Status);
                Assert.Null(saved.SubmittedAtUtc);
                Assert.Empty(saved.Items);

                Assert.Equal(
                    otherVersion,
                    Convert.ToBase64String(saved.RowVersion));
            }

            // 將 Employee B 的 Draft 轉為 Pending
            string pendingVersion;

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var otherRequest = await db.PurchaseRequests
                    .Include(request => request.Items)
                    .SingleAsync(request =>
                        request.Id == otherRequestId);

                var now = DateTimeOffset.UtcNow;

                otherRequest.Status = "Pending";
                otherRequest.SubmittedAtUtc = now;
                otherRequest.UpdatedAtUtc = now;

                otherRequest.Items.Add(new PurchaseRequestItem
                {
                    Name = "Employee B 的鍵盤",
                    Quantity = 2,
                    EstimatedUnitPrice = 1000m
                });

                await db.SaveChangesAsync();

                pendingVersion = Convert.ToBase64String(
                    otherRequest.RowVersion);
            }

            Assert.NotEqual(otherVersion, pendingVersion);

            // Employee A 嘗試查看 B 的 Pending 明細
            using var otherPendingResponse = await client.GetAsync(
                $"/api/purchase-requests/{otherRequestId}");

            Assert.Equal(
                HttpStatusCode.NotFound,
                otherPendingResponse.StatusCode);

            // Employee A 的 mine 清單仍不可出現 B 的申請
            using var mineResponse = await client.GetAsync(
                "/api/purchase-requests?view=mine");

            Assert.Equal(
                HttpStatusCode.OK,
                mineResponse.StatusCode);

            var mineJson = await mineResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            Assert.DoesNotContain(
                mineJson.EnumerateArray(),
                request => request.GetProperty("id").GetInt32()
                    == otherRequestId);

            // 確認 B 的 Pending 申請沒有被修改
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .SingleAsync(request =>
                        request.Id == otherRequestId);

                Assert.Equal("Pending", saved.Status);
                Assert.NotNull(saved.SubmittedAtUtc);
                Assert.Single(saved.Items);

                Assert.Equal(
                    "Employee B 的鍵盤",
                    saved.Items.Single().Name);

                Assert.Equal(
                    pendingVersion,
                    Convert.ToBase64String(saved.RowVersion));
            }
        }
        finally
        {
            if (createdUserIds.Count > 0)
            {
                using var scope = factory.Services.CreateScope();

                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                // 只清理本次建立的申請
                var requests = await db.PurchaseRequests
                    .Where(request =>
                        createdUserIds.Contains(request.RequesterId))
                    .ToListAsync();

                db.PurchaseRequests.RemoveRange(requests);
                await db.SaveChangesAsync();

                // 清理本次建立的使用者
                var userManager = scope.ServiceProvider
                    .GetRequiredService<UserManager<ApplicationUser>>();

                foreach (var userId in createdUserIds)
                {
                    var user = await userManager.FindByIdAsync(userId);

                    if (user is not null)
                    {
                        var result = await userManager.DeleteAsync(user);
                        Assert.True(result.Succeeded);
                    }
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

        Assert.Equal(
            "PurchaseFlowTestsDb",
            settings.InitialCatalog);

        Assert.Equal(
            @".\SQLEXPRESS",
            settings.DataSource);
    }
}
