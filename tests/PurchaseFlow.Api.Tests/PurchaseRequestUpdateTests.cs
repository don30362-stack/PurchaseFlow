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

public class PurchaseRequestUpdateTests
{
    [Fact]
    public async Task Employee_UpdatesDraft_AndStaleVersionReturns409()
    {
        using var factory = new SqlServerTestApiFactory();

        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        var userName = $"put-test-{Guid.NewGuid():N}";
        const string password = "TestPassword!4286";

        string? userId = null;
        int requestId;
        string originalVersion;

        try
        {
            // 建立測試 Employee 與一張草稿
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
                    var roleResult = await roleManager.CreateAsync(
                        new IdentityRole("Employee"));

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
                    user, "Employee");

                Assert.True(roleResult2.Succeeded);

                var now = DateTimeOffset.UtcNow;

                var draft = new PurchaseRequest
                {
                    RequesterId = userId,
                    Title = "修改前草稿",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Items =
                    [
                        new PurchaseRequestItem
                        {
                            Name = "舊品項",
                            Quantity = 1,
                            EstimatedUnitPrice = 100m
                        }
                    ]
                };

                db.PurchaseRequests.Add(draft);
                await db.SaveChangesAsync();

                requestId = draft.Id;
                originalVersion = Convert.ToBase64String(
                    draft.RowVersion);
            }

            // Employee 登入
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

            loginRequest.Headers.Add(
                "X-CSRF-TOKEN", loginCsrf);

            using var loginResponse =
                await client.SendAsync(loginRequest);

            Assert.Equal(
                HttpStatusCode.OK,
                loginResponse.StatusCode);

            var updateCsrf = await GetCsrfTokenAsync(client);

            // 第一次 PUT：使用正確的版本
            using var updateRequest = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/purchase-requests/{requestId}")
            {
                Content = JsonContent.Create(new
                {
                    title = "修改後草稿",
                    purpose = "測試完整替換品項",
                    rowVersion = originalVersion,
                    items = new[]
                    {
                        new
                        {
                            name = "鍵盤",
                            quantity = 2,
                            estimatedUnitPrice = 1000m
                        },
                        new
                        {
                            name = "滑鼠",
                            quantity = 1,
                            estimatedUnitPrice = 500m
                        }
                    }
                })
            };

            updateRequest.Headers.Add(
                "X-CSRF-TOKEN", updateCsrf);

            using var updateResponse =
                await client.SendAsync(updateRequest);

            Assert.Equal(
                HttpStatusCode.OK,
                updateResponse.StatusCode);

            var updated = await updateResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            Assert.Equal(
                2500m,
                updated.GetProperty("totalAmount").GetDecimal());

            var newVersion = updated
                .GetProperty("rowVersion").GetString();

            Assert.False(string.IsNullOrWhiteSpace(newVersion));
            Assert.NotEqual(originalVersion, newVersion);

            Assert.Equal(
                2,
                updated.GetProperty("items").GetArrayLength());

            // 第二次 PUT：故意使用過期版本
            using var staleRequest = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/purchase-requests/{requestId}")
            {
                Content = JsonContent.Create(new
                {
                    title = "不應成功覆蓋",
                    rowVersion = originalVersion,
                    items = new[]
                    {
                        new
                        {
                            name = "錯誤覆蓋品項",
                            quantity = 1,
                            estimatedUnitPrice = 9999m
                        }
                    }
                })
            };

            staleRequest.Headers.Add(
                "X-CSRF-TOKEN", updateCsrf);

            using var staleResponse =
                await client.SendAsync(staleRequest);

            Assert.Equal(
                HttpStatusCode.Conflict,
                staleResponse.StatusCode);

            // 直接查詢 SQL Server，確認舊版本未覆蓋新資料
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
                Assert.Equal("修改後草稿", saved.Title);
                Assert.Equal(2, saved.Items.Count);

                Assert.DoesNotContain(
                    saved.Items,
                    item => item.Name == "舊品項");

                Assert.DoesNotContain(
                    saved.Items,
                    item => item.Name == "錯誤覆蓋品項");

                var total = saved.Items.Sum(
                    item => item.Quantity *
                            item.EstimatedUnitPrice);

                Assert.Equal(2500m, total);

                Assert.Equal(
                    newVersion,
                    Convert.ToBase64String(saved.RowVersion));
            }


            // 將測試草稿改成 Pending，模擬已送審狀態
            string pendingVersion;

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var draft = await db.PurchaseRequests
                    .SingleAsync(request => request.Id == requestId);

                var now = DateTimeOffset.UtcNow;

                draft.Status = "Pending";
                draft.SubmittedAtUtc = now;
                draft.UpdatedAtUtc = now;

                await db.SaveChangesAsync();

                pendingVersion = Convert.ToBase64String(
                    draft.RowVersion);
            }

            // 使用 Pending 的最新版本嘗試修改
            using var pendingRequest = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/purchase-requests/{requestId}")
            {
                Content = JsonContent.Create(new
                {
                    title = "不應修改 Pending",
                    purpose = "此內容不應寫入",
                    rowVersion = pendingVersion,
                    items = new[]
                    {
                        new
                        {
                            name = "錯誤修改品項",
                            quantity = 1,
                            estimatedUnitPrice = 9999m
                        }
                    }
                })
            };

            pendingRequest.Headers.Add(
                "X-CSRF-TOKEN", updateCsrf);

            using var pendingResponse =
                await client.SendAsync(pendingRequest);

            Assert.Equal(
                HttpStatusCode.Conflict,
                pendingResponse.StatusCode);

            // 確認 Pending 資料沒有被 PUT 修改
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .SingleAsync(request => request.Id == requestId);

                Assert.Equal("Pending", saved.Status);
                Assert.Equal("修改後草稿", saved.Title);

                Assert.NotNull(saved.SubmittedAtUtc);

                Assert.Equal(
                    pendingVersion,
                    Convert.ToBase64String(saved.RowVersion));

                Assert.Equal(2, saved.Items.Count);

                var total = saved.Items.Sum(
                    item => item.Quantity *
                            item.EstimatedUnitPrice);

                Assert.Equal(2500m, total);

                Assert.DoesNotContain(
                    saved.Items,
                    item => item.Name == "錯誤修改品項");
            }


            // 驗證最終狀態不可修改
            foreach (var status in new[] { "Approved", "Rejected" })
            {
                string finalVersion;

                // 直接準備測試狀態
                using (var scope = factory.Services.CreateScope())
                {
                    var db = scope.ServiceProvider
                        .GetRequiredService<ApplicationDbContext>();

                    EnsureTestDatabase(db);

                    var draft = await db.PurchaseRequests
                        .SingleAsync(request => request.Id == requestId);

                    draft.Status = status;
                    draft.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await db.SaveChangesAsync();

                    finalVersion = Convert.ToBase64String(
                        draft.RowVersion);
                }

                // 使用最新版本嘗試修改
                using var finalRequest = new HttpRequestMessage(
                    HttpMethod.Put,
                    $"/api/purchase-requests/{requestId}")
                {
                    Content = JsonContent.Create(new
                    {
                        title = "不應修改最終狀態",
                        rowVersion = finalVersion,
                        items = Array.Empty<object>()
                    })
                };

                finalRequest.Headers.Add(
                    "X-CSRF-TOKEN", updateCsrf);

                using var finalResponse =
                    await client.SendAsync(finalRequest);

                Assert.Equal(
                    HttpStatusCode.Conflict,
                    finalResponse.StatusCode);

                // 驗證 SQL Server 資料未被修改
                using (var scope = factory.Services.CreateScope())
                {
                    var db = scope.ServiceProvider
                        .GetRequiredService<ApplicationDbContext>();

                    EnsureTestDatabase(db);

                    var saved = await db.PurchaseRequests
                        .AsNoTracking()
                        .Include(request => request.Items)
                        .SingleAsync(request => request.Id == requestId);

                    Assert.Equal(status, saved.Status);
                    Assert.Equal("修改後草稿", saved.Title);

                    Assert.Equal(
                        finalVersion,
                        Convert.ToBase64String(saved.RowVersion));

                    Assert.Equal(2, saved.Items.Count);

                    var total = saved.Items.Sum(
                        item => item.Quantity *
                                item.EstimatedUnitPrice);

                    Assert.Equal(2500m, total);
                }
            }
        }
        finally
        {
            // 清理本次測試的草稿與帳號
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
        var connectionString =
            db.Database.GetConnectionString()
            ?? throw new InvalidOperationException(
                "Missing database connection.");

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
