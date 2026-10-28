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
