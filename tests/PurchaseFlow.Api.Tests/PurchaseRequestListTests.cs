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

public class PurchaseRequestListTests
{
    [Fact]
    public async Task ManagerAndEmployee_CanOnlyUseAllowedListViews()
    {
        using var factory = new SqlServerTestApiFactory();

        var clientOptions = new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        };

        using var managerClient = factory.CreateClient(clientOptions);
        using var employeeClient = factory.CreateClient(clientOptions);

        var managerName = $"list-manager-{Guid.NewGuid():N}";
        var employeeName = $"list-employee-{Guid.NewGuid():N}";
        const string password = "TestPassword!4286";

        string? managerId = null;
        string? employeeId = null;

        int draftId = 0;
        int pendingId = 0;
        int approvedId = 0;
        int rejectedId = 0;

        try
        {
            // 準備測試帳號及四種狀態的申請
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var userManager = scope.ServiceProvider
                    .GetRequiredService<UserManager<ApplicationUser>>();

                var roleManager = scope.ServiceProvider
                    .GetRequiredService<RoleManager<IdentityRole>>();

                foreach (var role in new[] { "Employee", "Manager" })
                {
                    if (!await roleManager.RoleExistsAsync(role))
                    {
                        var result = await roleManager.CreateAsync(
                            new IdentityRole(role));

                        Assert.True(result.Succeeded);
                    }
                }

                var manager = new ApplicationUser
                {
                    UserName = managerName
                };

                Assert.True((await userManager.CreateAsync(
                    manager, password)).Succeeded);

                managerId = manager.Id;

                Assert.True((await userManager.AddToRoleAsync(
                    manager, "Manager")).Succeeded);

                var employee = new ApplicationUser
                {
                    UserName = employeeName
                };

                Assert.True((await userManager.CreateAsync(
                    employee, password)).Succeeded);

                employeeId = employee.Id;

                Assert.True((await userManager.AddToRoleAsync(
                    employee, "Employee")).Succeeded);

                var now = DateTimeOffset.UtcNow;

                PurchaseRequest MakeRequest(string status)
                {
                    return new PurchaseRequest
                    {
                        RequesterId = employee.Id,
                        Title = $"清單測試 {status}",
                        Status = status,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        SubmittedAtUtc =
                            status == "Draft" ? null : now
                    };
                }

                var draft = MakeRequest("Draft");
                var pending = MakeRequest("Pending");
                var approved = MakeRequest("Approved");
                var rejected = MakeRequest("Rejected");

                pending.Items.Add(new PurchaseRequestItem
                {
                    Name = "鍵盤",
                    Quantity = 2,
                    EstimatedUnitPrice = 1800m
                });

                pending.Items.Add(new PurchaseRequestItem
                {
                    Name = "滑鼠",
                    Quantity = 3,
                    EstimatedUnitPrice = 450m
                });

                approved.ApprovalLog = new ApprovalLog
                {
                    ReviewerId = manager.Id,
                    Decision = "Approved",
                    ReviewedAtUtc = now
                };

                rejected.ApprovalLog = new ApprovalLog
                {
                    ReviewerId = manager.Id,
                    Decision = "Rejected",
                    Comment = "預算資料不足",
                    ReviewedAtUtc = now
                };

                db.PurchaseRequests.AddRange(
                    draft, pending, approved, rejected);

                await db.SaveChangesAsync();

                draftId = draft.Id;
                pendingId = pending.Id;
                approvedId = approved.Id;
                rejectedId = rejected.Id;
            }

            // Manager 登入
            await LoginAsync(managerClient, managerName, password);

            // Manager 查詢 Pending
            var pendingJson = await GetListAsync(
                managerClient, "pending");

            var pendingRows = pendingJson.EnumerateArray().ToArray();
            var pendingIds = pendingRows
                .Select(row => row.GetProperty("id").GetInt32())
                .ToArray();

            Assert.Contains(pendingId, pendingIds);
            Assert.DoesNotContain(draftId, pendingIds);
            Assert.DoesNotContain(approvedId, pendingIds);
            Assert.DoesNotContain(rejectedId, pendingIds);

            Assert.All(pendingRows, row =>
                Assert.Equal(
                    "Pending",
                    row.GetProperty("status").GetString()));

            var ownPendingRow = pendingRows.Single(row =>
                row.GetProperty("id").GetInt32() == pendingId);

            Assert.Equal(
                4950m,
                ownPendingRow.GetProperty("totalAmount").GetDecimal());

            // Manager 查詢 Reviewed
            var reviewedJson = await GetListAsync(
                managerClient, "reviewed");

            var reviewedRows = reviewedJson.EnumerateArray().ToArray();
            var reviewedIds = reviewedRows
                .Select(row => row.GetProperty("id").GetInt32())
                .ToArray();

            Assert.Contains(approvedId, reviewedIds);
            Assert.Contains(rejectedId, reviewedIds);
            Assert.DoesNotContain(draftId, reviewedIds);
            Assert.DoesNotContain(pendingId, reviewedIds);

            Assert.All(reviewedRows, row =>
                Assert.Contains(
                    row.GetProperty("status").GetString(),
                    new[] { "Approved", "Rejected" }));

            // Manager 禁止使用 mine
            using var managerMine = await managerClient.GetAsync(
                "/api/purchase-requests?view=mine");

            Assert.Equal(
                HttpStatusCode.Forbidden,
                managerMine.StatusCode);

            // Employee 登入
            await LoginAsync(employeeClient, employeeName, password);

            // Employee 可以查詢自己的四張申請
            var mineJson = await GetListAsync(
                employeeClient, "mine");

            var mineIds = mineJson.EnumerateArray()
                .Select(row => row.GetProperty("id").GetInt32())
                .ToArray();

            Assert.Equal(4, mineIds.Length);
            Assert.Contains(draftId, mineIds);
            Assert.Contains(pendingId, mineIds);
            Assert.Contains(approvedId, mineIds);
            Assert.Contains(rejectedId, mineIds);

            // Employee 禁止使用主管查詢
            foreach (var view in new[] { "pending", "reviewed" })
            {
                using var response = await employeeClient.GetAsync(
                    $"/api/purchase-requests?view={view}");

                Assert.Equal(
                    HttpStatusCode.Forbidden,
                    response.StatusCode);
            }

            // Manager 可以查看 Pending 明細
            using var managerPendingResponse =
                await managerClient.GetAsync(
                    $"/api/purchase-requests/{pendingId}");

            Assert.Equal(
                HttpStatusCode.OK,
                managerPendingResponse.StatusCode);

            var pendingDetail = await managerPendingResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            Assert.Equal(
                "Pending",
                pendingDetail.GetProperty("status").GetString());

            Assert.Equal(
                4950m,
                pendingDetail.GetProperty("totalAmount").GetDecimal());

            Assert.Equal(
                2,
                pendingDetail.GetProperty("items").GetArrayLength());

            Assert.Equal(
                JsonValueKind.Null,
                pendingDetail.GetProperty("approvalLog").ValueKind);

            // Manager 不可以查看 Draft 明細
            using var managerDraftResponse =
                await managerClient.GetAsync(
                    $"/api/purchase-requests/{draftId}");

            Assert.Equal(
                HttpStatusCode.NotFound,
                managerDraftResponse.StatusCode);

            // Manager 可以查看已審核明細及審核紀錄
            var reviewedDetails = new[]
            {
                (Id: approvedId,
                 Decision: "Approved",
                 Comment: (string?)null),

                (Id: rejectedId,
                 Decision: "Rejected",
                 Comment: (string?)"預算資料不足")
            };

            foreach (var scenario in reviewedDetails)
            {
                using var response = await managerClient.GetAsync(
                    $"/api/purchase-requests/{scenario.Id}");

                Assert.Equal(
                    HttpStatusCode.OK,
                    response.StatusCode);

                var detail = await response.Content
                    .ReadFromJsonAsync<JsonElement>();

                Assert.Equal(
                    scenario.Decision,
                    detail.GetProperty("status").GetString());

                var log = detail.GetProperty("approvalLog");

                Assert.Equal(JsonValueKind.Object, log.ValueKind);

                Assert.Equal(
                    managerId,
                    log.GetProperty("reviewerId").GetString());

                Assert.Equal(
                    scenario.Decision,
                    log.GetProperty("decision").GetString());

                Assert.Equal(
                    scenario.Comment,
                    log.GetProperty("comment").GetString());

                Assert.NotEqual(
                    JsonValueKind.Null,
                    log.GetProperty("reviewedAtUtc").ValueKind);
            }

            // Employee 仍可以查看自己的 Draft
            using var employeeDraftResponse =
                await employeeClient.GetAsync(
                    $"/api/purchase-requests/{draftId}");

            Assert.Equal(
                HttpStatusCode.OK,
                employeeDraftResponse.StatusCode);

            var employeeDraft = await employeeDraftResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            Assert.Equal(
                "Draft",
                employeeDraft.GetProperty("status").GetString());
        }
        finally
        {
            // 清理本次測試建立的資料
            using var scope = factory.Services.CreateScope();

            var db = scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>();

            EnsureTestDatabase(db);

            if (employeeId is not null)
            {
                var requestIds = await db.PurchaseRequests
                    .Where(request => request.RequesterId == employeeId)
                    .Select(request => request.Id)
                    .ToArrayAsync();

                // 外鍵 NoAction：必須先刪審核紀錄
                var logs = await db.ApprovalLogs
                    .Where(log => requestIds.Contains(log.PurchaseRequestId))
                    .ToListAsync();

                db.ApprovalLogs.RemoveRange(logs);
                await db.SaveChangesAsync();

                var requests = await db.PurchaseRequests
                    .Where(request => request.RequesterId == employeeId)
                    .ToListAsync();

                db.PurchaseRequests.RemoveRange(requests);
                await db.SaveChangesAsync();
            }

            var userManager = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();

            foreach (var id in new[] { employeeId, managerId })
            {
                if (id is null)
                {
                    continue;
                }

                var user = await userManager.FindByIdAsync(id);

                if (user is not null)
                {
                    Assert.True(
                        (await userManager.DeleteAsync(user)).Succeeded);
                }
            }
        }
    }

    private static async Task LoginAsync(
        HttpClient client,
        string userName,
        string password)
    {
        var csrfJson = await client.GetFromJsonAsync<JsonElement>(
            "/api/auth/csrf");

        var csrfToken = csrfJson
            .GetProperty("csrfToken").GetString();

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new
            {
                userName,
                password
            })
        };

        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);
    }

    private static async Task<JsonElement> GetListAsync(
        HttpClient client,
        string view)
    {
        using var response = await client.GetAsync(
            $"/api/purchase-requests?view={view}");

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
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
