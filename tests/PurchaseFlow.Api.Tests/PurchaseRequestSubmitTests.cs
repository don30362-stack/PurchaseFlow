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

public class PurchaseRequestSubmitTests
{
    [Fact]
    public async Task Employee_CanSubmitDraft_ButCannotSubmitTwice()
    {
        using var factory = new SqlServerTestApiFactory();

        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        var userName = $"submit-test-{Guid.NewGuid():N}";
        const string password = "TestPassword!4286";

        string? userId = null;
        int requestId;
        string originalVersion;

        try
        {
            // 建立測試 Employee 與含兩個品項的 Draft
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
                    Title = "自動化送審測試",
                    Purpose = "測試 Draft 轉 Pending",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Items =
                    [
                        new PurchaseRequestItem
                        {
                            Name = "鍵盤",
                            Quantity = 2,
                            EstimatedUnitPrice = 1800m
                        },
                        new PurchaseRequestItem
                        {
                            Name = "滑鼠",
                            Quantity = 3,
                            EstimatedUnitPrice = 450m
                        }
                    ]
                };

                db.PurchaseRequests.Add(draft);
                await db.SaveChangesAsync();

                requestId = draft.Id;
                originalVersion = Convert.ToBase64String(
                    draft.RowVersion);
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

            loginRequest.Headers.Add(
                "X-CSRF-TOKEN", loginCsrf);

            using var loginResponse =
                await client.SendAsync(loginRequest);

            Assert.Equal(
                HttpStatusCode.OK,
                loginResponse.StatusCode);

            // 登入後重新取得 CSRF Token
            var submitCsrf = await GetCsrfTokenAsync(client);


            // 已登入，但故意不提供 CSRF Token
            using var noCsrfRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/purchase-requests/{requestId}/submit")
            {
                Content = JsonContent.Create(new
                {
                    rowVersion = originalVersion
                })
            };

            // 刻意不加入 X-CSRF-TOKEN Header
            using var noCsrfResponse =
                await client.SendAsync(noCsrfRequest);

            Assert.Equal(
                HttpStatusCode.BadRequest,
                noCsrfResponse.StatusCode);

            // 確認缺少 CSRF Token 沒有改變資料庫
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .SingleAsync(request => request.Id == requestId);

                Assert.Equal("Draft", saved.Status);
                Assert.Null(saved.SubmittedAtUtc);
                Assert.Equal(2, saved.Items.Count);

                Assert.Equal(
                    originalVersion,
                    Convert.ToBase64String(saved.RowVersion));
            }

            // 建立沒有品項的 Draft
            int emptyDraftId;
            string emptyDraftVersion;

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var now = DateTimeOffset.UtcNow;

                var emptyDraft = new PurchaseRequest
                {
                    RequesterId = userId!,
                    Title = "空草稿送審測試",
                    Status = "Draft",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    Items = []
                };

                db.PurchaseRequests.Add(emptyDraft);
                await db.SaveChangesAsync();

                emptyDraftId = emptyDraft.Id;
                emptyDraftVersion = Convert.ToBase64String(
                    emptyDraft.RowVersion);
            }

            // 使用有效 CSRF Token 嘗試送審空草稿
            using var emptySubmitRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/purchase-requests/{emptyDraftId}/submit")
            {
                Content = JsonContent.Create(new
                {
                    rowVersion = emptyDraftVersion
                })
            };

            emptySubmitRequest.Headers.Add(
                "X-CSRF-TOKEN", submitCsrf);

            using var emptySubmitResponse =
                await client.SendAsync(emptySubmitRequest);

            Assert.Equal(
                HttpStatusCode.BadRequest,
                emptySubmitResponse.StatusCode);

            // 確認空草稿沒有被送審
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .SingleAsync(request =>
                        request.Id == emptyDraftId);

                Assert.Equal("Draft", saved.Status);
                Assert.Equal("空草稿送審測試", saved.Title);

                Assert.Empty(saved.Items);
                Assert.Null(saved.SubmittedAtUtc);

                Assert.Equal(
                    emptyDraftVersion,
                    Convert.ToBase64String(saved.RowVersion));
            }

            // 模擬草稿已被其他操作修改
            string currentVersion;

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var editedDraft = await db.PurchaseRequests
                    .Include(request => request.Items)
                    .SingleAsync(request =>
                        request.Id == emptyDraftId);

                editedDraft.Items.Add(new PurchaseRequestItem
                {
                    Name = "測試鍵盤",
                    Quantity = 1,
                    EstimatedUnitPrice = 100m
                });

                editedDraft.UpdatedAtUtc = DateTimeOffset.UtcNow;

                // 即使只變更品項，也要更新主單版本
                db.Entry(editedDraft)
                    .Property(request => request.UpdatedAtUtc)
                    .IsModified = true;

                await db.SaveChangesAsync();

                currentVersion = Convert.ToBase64String(
                    editedDraft.RowVersion);
            }

            Assert.NotEqual(emptyDraftVersion, currentVersion);

            // 使用舊 RowVersion 嘗試送審
            using var staleSubmitRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/purchase-requests/{emptyDraftId}/submit")
            {
                Content = JsonContent.Create(new
                {
                    rowVersion = emptyDraftVersion
                })
            };

            staleSubmitRequest.Headers.Add(
                "X-CSRF-TOKEN", submitCsrf);

            using var staleSubmitResponse =
                await client.SendAsync(staleSubmitRequest);

            Assert.Equal(
                HttpStatusCode.Conflict,
                staleSubmitResponse.StatusCode);

            // 確認過期版本沒有改變資料庫
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .SingleAsync(request =>
                        request.Id == emptyDraftId);

                Assert.Equal("Draft", saved.Status);
                Assert.Null(saved.SubmittedAtUtc);
                Assert.Equal("空草稿送審測試", saved.Title);

                Assert.Single(saved.Items);

                var item = saved.Items.Single();

                Assert.Equal("測試鍵盤", item.Name);
                Assert.Equal(1, item.Quantity);
                Assert.Equal(100m, item.EstimatedUnitPrice);

                Assert.Equal(
                    currentVersion,
                    Convert.ToBase64String(saved.RowVersion));
            }

            // 第一次送審：Draft -> Pending
            using var submitRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/purchase-requests/{requestId}/submit")
            {
                Content = JsonContent.Create(new
                {
                    rowVersion = originalVersion
                })
            };

            submitRequest.Headers.Add(
                "X-CSRF-TOKEN", submitCsrf);

            using var submitResponse =
                await client.SendAsync(submitRequest);

            Assert.Equal(
                HttpStatusCode.OK,
                submitResponse.StatusCode);

            var submitted = await submitResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            Assert.Equal(
                "Pending",
                submitted.GetProperty("status").GetString());

            Assert.Equal(
                4950m,
                submitted.GetProperty("totalAmount").GetDecimal());

            Assert.Equal(
                2,
                submitted.GetProperty("items").GetArrayLength());

            Assert.NotEqual(
                JsonValueKind.Null,
                submitted.GetProperty("submittedAtUtc").ValueKind);

            var newVersion = submitted
                .GetProperty("rowVersion").GetString();

            Assert.False(string.IsNullOrWhiteSpace(newVersion));
            Assert.NotEqual(originalVersion, newVersion);

            // 重複送審：即使使用最新版本，也必須拒絕
            using var repeatRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/purchase-requests/{requestId}/submit")
            {
                Content = JsonContent.Create(new
                {
                    rowVersion = newVersion
                })
            };

            repeatRequest.Headers.Add(
                "X-CSRF-TOKEN", submitCsrf);

            using var repeatResponse =
                await client.SendAsync(repeatRequest);

            Assert.Equal(
                HttpStatusCode.Conflict,
                repeatResponse.StatusCode);

            // 直接查詢 SQL Server，確認送審結果
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
                Assert.Equal("Pending", saved.Status);
                Assert.Equal("自動化送審測試", saved.Title);

                Assert.NotNull(saved.SubmittedAtUtc);
                Assert.Equal(2, saved.Items.Count);

                var total = saved.Items.Sum(
                    item => item.Quantity *
                            item.EstimatedUnitPrice);

                Assert.Equal(4950m, total);

                Assert.Equal(
                    newVersion,
                    Convert.ToBase64String(saved.RowVersion));
            }
        }
        finally
        {
            // 只清理本次測試建立的資料
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

        Assert.Equal(
            "PurchaseFlowTestsDb",
            settings.InitialCatalog);

        Assert.Equal(
            @".\SQLEXPRESS",
            settings.DataSource);
    }
}
