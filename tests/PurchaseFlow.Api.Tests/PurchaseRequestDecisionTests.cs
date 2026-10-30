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

public class PurchaseRequestDecisionTests
{
    [Fact]
    public async Task Manager_CanApprovePending_ButCannotApproveTwice()
    {
        using var factory = new SqlServerTestApiFactory();

        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        var managerName = $"decision-manager-{Guid.NewGuid():N}";
        var employeeName = $"decision-employee-{Guid.NewGuid():N}";
        const string password = "TestPassword!4286";

        string? managerId = null;
        string? employeeId = null;
        int requestId = 0;
        string originalVersion = "";

        try
        {
            // 建立測試帳號與 Pending 申請
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

                var pending = new PurchaseRequest
                {
                    RequesterId = employee.Id,
                    Title = "核准整合測試",
                    Status = "Pending",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    SubmittedAtUtc = now,
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

                db.PurchaseRequests.Add(pending);
                await db.SaveChangesAsync();

                requestId = pending.Id;
                originalVersion = Convert.ToBase64String(
                    pending.RowVersion);
            }

            // Manager 登入，取得有效 Cookie
            var loginCsrf = await GetCsrfTokenAsync(client);

            using var loginRequest = new HttpRequestMessage(
                HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new
                {
                    userName = managerName,
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

            var decisionCsrf = await GetCsrfTokenAsync(client);

            // 第一次核准：Pending -> Approved
            using var decisionRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/purchase-requests/{requestId}/decision")
            {
                Content = JsonContent.Create(new
                {
                    decision = "Approved",
                    comment = "核准採購",
                    rowVersion = originalVersion
                })
            };

            decisionRequest.Headers.Add(
                "X-CSRF-TOKEN", decisionCsrf);

            using var decisionResponse =
                await client.SendAsync(decisionRequest);

            Assert.Equal(
                HttpStatusCode.OK,
                decisionResponse.StatusCode);

            var approved = await decisionResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            Assert.Equal(
                "Approved",
                approved.GetProperty("status").GetString());

            Assert.Equal(
                4950m,
                approved.GetProperty("totalAmount").GetDecimal());

            Assert.Equal(
                2,
                approved.GetProperty("items").GetArrayLength());

            var log = approved.GetProperty("approvalLog");

            Assert.Equal(
                "Approved",
                log.GetProperty("decision").GetString());

            Assert.Equal(
                "核准採購",
                log.GetProperty("comment").GetString());

            Assert.Equal(
                managerId,
                log.GetProperty("reviewerId").GetString());

            Assert.NotEqual(
                JsonValueKind.Null,
                log.GetProperty("reviewedAtUtc").ValueKind);

            var newVersion = approved
                .GetProperty("rowVersion").GetString();

            Assert.False(string.IsNullOrWhiteSpace(newVersion));
            Assert.NotEqual(originalVersion, newVersion);

            // 使用最新 RowVersion 重複核准
            using var repeatRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/purchase-requests/{requestId}/decision")
            {
                Content = JsonContent.Create(new
                {
                    decision = "Approved",
                    comment = "第二次核准",
                    rowVersion = newVersion
                })
            };

            repeatRequest.Headers.Add(
                "X-CSRF-TOKEN", decisionCsrf);

            using var repeatResponse =
                await client.SendAsync(repeatRequest);

            Assert.Equal(
                HttpStatusCode.Conflict,
                repeatResponse.StatusCode);

            // 直接查詢 SQL Server，確認資料一致
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .Include(request => request.ApprovalLog)
                    .SingleAsync(request => request.Id == requestId);

                Assert.Equal("Approved", saved.Status);
                Assert.Equal(employeeId, saved.RequesterId);
                Assert.NotNull(saved.SubmittedAtUtc);

                Assert.Equal(2, saved.Items.Count);

                Assert.Equal(
                    4950m,
                    saved.Items.Sum(item =>
                        item.Quantity * item.EstimatedUnitPrice));

                Assert.Equal(
                    newVersion,
                    Convert.ToBase64String(saved.RowVersion));

                Assert.NotNull(saved.ApprovalLog);

                Assert.Equal(
                    "Approved",
                    saved.ApprovalLog!.Decision);

                Assert.Equal(
                    "核准採購",
                    saved.ApprovalLog.Comment);

                Assert.Equal(
                    managerId,
                    saved.ApprovalLog.ReviewerId);

                var logCount = await db.ApprovalLogs
                    .CountAsync(log =>
                        log.PurchaseRequestId == requestId);

                Assert.Equal(1, logCount);
            }
        }
        finally
        {
            // 清理本次測試建立的資料
            using var scope = factory.Services.CreateScope();

            var db = scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>();

            EnsureTestDatabase(db);

            if (requestId > 0)
            {
                // ApprovalLogs 使用 NoAction，必須先刪紀錄
                var logs = await db.ApprovalLogs
                    .Where(log =>
                        log.PurchaseRequestId == requestId)
                    .ToListAsync();

                db.ApprovalLogs.RemoveRange(logs);
                await db.SaveChangesAsync();

                var request = await db.PurchaseRequests
                    .SingleOrDefaultAsync(item =>
                        item.Id == requestId);

                if (request is not null)
                {
                    db.PurchaseRequests.Remove(request);
                    await db.SaveChangesAsync();
                }
            }

            var userManager = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();

            foreach (var userId in new[] { employeeId, managerId })
            {
                if (userId is null)
                {
                    continue;
                }

                var user = await userManager.FindByIdAsync(userId);

                if (user is not null)
                {
                    Assert.True(
                        (await userManager.DeleteAsync(user)).Succeeded);
                }
            }
        }
    }

    [Fact]
    public async Task Manager_CanRejectPending_AndInvalidRequestsDoNotChangeData()
    {
        using var factory = new SqlServerTestApiFactory();

        var clientOptions = new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        };

        using var managerClient = factory.CreateClient(clientOptions);
        using var employeeClient = factory.CreateClient(clientOptions);

        var managerName = $"reject-manager-{Guid.NewGuid():N}";
        var employeeName = $"reject-employee-{Guid.NewGuid():N}";
        const string password = "TestPassword!4286";
        const string reason = "請補充採購用途與設備規格";

        string? managerId = null;
        string? employeeId = null;
        int requestId = 0;
        string originalVersion = "";

        try
        {
            // 建立測試帳號與 Pending 申請
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

                var pending = new PurchaseRequest
                {
                    RequesterId = employee.Id,
                    Title = "拒絕整合測試",
                    Purpose = "辦公設備採購",
                    Status = "Pending",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    SubmittedAtUtc = now,
                    Items =
                    [
                        new PurchaseRequestItem
                        {
                            Name = "辦公椅",
                            Quantity = 2,
                            EstimatedUnitPrice = 1500m
                        }
                    ]
                };

                db.PurchaseRequests.Add(pending);
                await db.SaveChangesAsync();

                requestId = pending.Id;
                originalVersion = Convert.ToBase64String(
                    pending.RowVersion);
            }

            // 分別登入 Manager、Employee
            await LoginAsync(
                managerClient, managerName, password);

            await LoginAsync(
                employeeClient, employeeName, password);

            var managerCsrf = await GetCsrfTokenAsync(managerClient);
            var employeeCsrf = await GetCsrfTokenAsync(employeeClient);

            // 1. Employee 不得審核
            using (var response = await SendDecisionAsync(
                employeeClient,
                requestId,
                employeeCsrf,
                "Approved",
                "越權核准",
                originalVersion))
            {
                Assert.Equal(
                    HttpStatusCode.Forbidden,
                    response.StatusCode);
            }

            // 2. Manager 使用非法 Decision
            using (var response = await SendDecisionAsync(
                managerClient,
                requestId,
                managerCsrf,
                "Cancelled",
                "非法決定",
                originalVersion))
            {
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    response.StatusCode);
            }

            // 3. 拒絕理由只有空白
            using (var response = await SendDecisionAsync(
                managerClient,
                requestId,
                managerCsrf,
                "Rejected",
                "   ",
                originalVersion))
            {
                Assert.Equal(
                    HttpStatusCode.BadRequest,
                    response.StatusCode);
            }

            // 確認前三次失敗沒有修改資料
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.ApprovalLog)
                    .SingleAsync(request => request.Id == requestId);

                Assert.Equal("Pending", saved.Status);
                Assert.Null(saved.ApprovalLog);

                Assert.Equal(
                    originalVersion,
                    Convert.ToBase64String(saved.RowVersion));

                Assert.Equal(
                    0,
                    await db.ApprovalLogs.CountAsync(log =>
                        log.PurchaseRequestId == requestId));
            }

            // 模擬另一個請求已更新資料，使原版本過期
            string currentVersion;

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .SingleAsync(request => request.Id == requestId);

                saved.Purpose = "已更新的採購說明";
                saved.UpdatedAtUtc = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync();

                currentVersion = Convert.ToBase64String(
                    saved.RowVersion);
            }

            Assert.NotEqual(originalVersion, currentVersion);

            // 4. Manager 使用過期 RowVersion
            using (var response = await SendDecisionAsync(
                managerClient,
                requestId,
                managerCsrf,
                "Rejected",
                reason,
                originalVersion))
            {
                Assert.Equal(
                    HttpStatusCode.Conflict,
                    response.StatusCode);
            }

            // 確認版本衝突沒有新增審核紀錄
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .SingleAsync(request => request.Id == requestId);

                Assert.Equal("Pending", saved.Status);

                Assert.Equal(
                    currentVersion,
                    Convert.ToBase64String(saved.RowVersion));

                Assert.Equal(
                    0,
                    await db.ApprovalLogs.CountAsync(log =>
                        log.PurchaseRequestId == requestId));
            }

            // 5. 使用最新 RowVersion 正常拒絕
            using var rejectResponse = await SendDecisionAsync(
                managerClient,
                requestId,
                managerCsrf,
                "Rejected",
                $"  {reason}  ",
                currentVersion);

            Assert.Equal(
                HttpStatusCode.OK,
                rejectResponse.StatusCode);

            var rejected = await rejectResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            Assert.Equal(
                "Rejected",
                rejected.GetProperty("status").GetString());

            Assert.Equal(
                3000m,
                rejected.GetProperty("totalAmount").GetDecimal());

            var log = rejected.GetProperty("approvalLog");

            Assert.Equal(
                "Rejected",
                log.GetProperty("decision").GetString());

            Assert.Equal(
                reason,
                log.GetProperty("comment").GetString());

            Assert.Equal(
                managerId,
                log.GetProperty("reviewerId").GetString());

            Assert.NotEqual(
                JsonValueKind.Null,
                log.GetProperty("reviewedAtUtc").ValueKind);

            var rejectedVersion = rejected
                .GetProperty("rowVersion").GetString();

            Assert.NotEqual(currentVersion, rejectedVersion);

            // 6. Employee 能查看自己的拒絕理由
            using var employeeResponse = await employeeClient.GetAsync(
                $"/api/purchase-requests/{requestId}");

            Assert.Equal(
                HttpStatusCode.OK,
                employeeResponse.StatusCode);

            var employeeDetail = await employeeResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            Assert.Equal(
                "Rejected",
                employeeDetail.GetProperty("status").GetString());

            Assert.Equal(
                reason,
                employeeDetail.GetProperty("approvalLog")
                    .GetProperty("comment").GetString());

            // 7. SQL Server 最終一致性
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .Include(request => request.ApprovalLog)
                    .SingleAsync(request => request.Id == requestId);

                Assert.Equal("Rejected", saved.Status);
                Assert.Equal(employeeId, saved.RequesterId);
                Assert.Equal("已更新的採購說明", saved.Purpose);
                Assert.Single(saved.Items);

                Assert.Equal(
                    3000m,
                    saved.Items.Sum(item =>
                        item.Quantity * item.EstimatedUnitPrice));

                Assert.Equal(
                    rejectedVersion,
                    Convert.ToBase64String(saved.RowVersion));

                Assert.NotNull(saved.ApprovalLog);
                Assert.Equal("Rejected", saved.ApprovalLog!.Decision);
                Assert.Equal(reason, saved.ApprovalLog.Comment);
                Assert.Equal(managerId, saved.ApprovalLog.ReviewerId);

                Assert.Equal(
                    1,
                    await db.ApprovalLogs.CountAsync(log =>
                        log.PurchaseRequestId == requestId));
            }
        }
        finally
        {
            using var scope = factory.Services.CreateScope();

            var db = scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>();

            EnsureTestDatabase(db);

            if (requestId > 0)
            {
                // 先刪 ApprovalLog，再刪主單
                var logs = await db.ApprovalLogs
                    .Where(log => log.PurchaseRequestId == requestId)
                    .ToListAsync();

                db.ApprovalLogs.RemoveRange(logs);
                await db.SaveChangesAsync();

                var request = await db.PurchaseRequests
                    .SingleOrDefaultAsync(item => item.Id == requestId);

                if (request is not null)
                {
                    db.PurchaseRequests.Remove(request);
                    await db.SaveChangesAsync();
                }
            }

            var userManager = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();

            foreach (var userId in new[] { employeeId, managerId })
            {
                if (userId is null)
                {
                    continue;
                }

                var user = await userManager.FindByIdAsync(userId);

                if (user is not null)
                {
                    Assert.True(
                        (await userManager.DeleteAsync(user)).Succeeded);
                }
            }
        }
    }

    [Fact]
    public async Task TwoManagers_ConcurrentDecisions_OnlyOneSucceeds()
    {
        using var factory = new SqlServerTestApiFactory();

        var options = new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        };

        using var managerAClient = factory.CreateClient(options);
        using var managerBClient = factory.CreateClient(options);

        var managerAName = $"concurrent-a-{Guid.NewGuid():N}";
        var managerBName = $"concurrent-b-{Guid.NewGuid():N}";
        var employeeName = $"concurrent-employee-{Guid.NewGuid():N}";

        const string password = "TestPassword!4286";
        const string approveComment = "核准採購";
        const string rejectComment = "預算不足";

        string? managerAId = null;
        string? managerBId = null;
        string? employeeId = null;

        int requestId = 0;
        string initialVersion = "";

        try
        {
            // 建立兩位主管、一位員工，以及一張 Pending 申請
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

                var managerA = new ApplicationUser
                {
                    UserName = managerAName
                };

                Assert.True((await userManager.CreateAsync(
                    managerA, password)).Succeeded);

                managerAId = managerA.Id;

                Assert.True((await userManager.AddToRoleAsync(
                    managerA, "Manager")).Succeeded);

                var managerB = new ApplicationUser
                {
                    UserName = managerBName
                };

                Assert.True((await userManager.CreateAsync(
                    managerB, password)).Succeeded);

                managerBId = managerB.Id;

                Assert.True((await userManager.AddToRoleAsync(
                    managerB, "Manager")).Succeeded);

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

                var pending = new PurchaseRequest
                {
                    RequesterId = employee.Id,
                    Title = "並行審核整合測試",
                    Purpose = "測試兩位主管同時審核",
                    Status = "Pending",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    SubmittedAtUtc = now,
                    Items =
                    [
                        new PurchaseRequestItem
                        {
                            Name = "辦公設備",
                            Quantity = 2,
                            EstimatedUnitPrice = 1200m
                        }
                    ]
                };

                db.PurchaseRequests.Add(pending);
                await db.SaveChangesAsync();

                requestId = pending.Id;
                initialVersion = Convert.ToBase64String(
                    pending.RowVersion);
            }

            // 兩位主管使用各自的 Cookie Session 登入
            await LoginAsync(
                managerAClient, managerAName, password);

            await LoginAsync(
                managerBClient, managerBName, password);

            var csrfA = await GetCsrfTokenAsync(managerAClient);
            var csrfB = await GetCsrfTokenAsync(managerBClient);

            // 兩個請求等待同一個開始訊號
            var startGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<HttpResponseMessage> SendAfterSignalAsync(
                HttpClient client,
                string csrf,
                string decision,
                string comment)
            {
                await startGate.Task;

                return await SendDecisionAsync(
                    client,
                    requestId,
                    csrf,
                    decision,
                    comment,
                    initialVersion);
            }

            var approveTask = SendAfterSignalAsync(
                managerAClient,
                csrfA,
                "Approved",
                approveComment);

            var rejectTask = SendAfterSignalAsync(
                managerBClient,
                csrfB,
                "Rejected",
                rejectComment);

            // 同時放行兩個 HTTP 請求
            startGate.SetResult(true);

            await Task.WhenAll(approveTask, rejectTask);

            using var approveResponse = await approveTask;
            using var rejectResponse = await rejectTask;

            var approveStatus = approveResponse.StatusCode;
            var rejectStatus = rejectResponse.StatusCode;

            // 必須恰好一個成功、一個版本衝突
            var validResult =
                (approveStatus == HttpStatusCode.OK &&
                 rejectStatus == HttpStatusCode.Conflict)
                ||
                (approveStatus == HttpStatusCode.Conflict &&
                 rejectStatus == HttpStatusCode.OK);

            Assert.True(
                validResult,
                $"核准 HTTP {(int)approveStatus}，" +
                $"拒絕 HTTP {(int)rejectStatus}；" +
                "預期恰好一個 200、一個 409。");

            // 不預設誰先成功，根據實際回應判斷勝出者
            var approveWon = approveStatus == HttpStatusCode.OK;

            var expectedDecision = approveWon
                ? "Approved"
                : "Rejected";

            var expectedComment = approveWon
                ? approveComment
                : rejectComment;

            var expectedReviewerId = approveWon
                ? managerAId
                : managerBId;

            var successResponse = approveWon
                ? approveResponse
                : rejectResponse;

            var success = await successResponse.Content
                .ReadFromJsonAsync<JsonElement>();

            Assert.Equal(
                expectedDecision,
                success.GetProperty("status").GetString());

            Assert.Equal(
                2400m,
                success.GetProperty("totalAmount").GetDecimal());

            var successLog = success.GetProperty("approvalLog");

            Assert.Equal(
                expectedDecision,
                successLog.GetProperty("decision").GetString());

            Assert.Equal(
                expectedComment,
                successLog.GetProperty("comment").GetString());

            Assert.Equal(
                expectedReviewerId,
                successLog.GetProperty("reviewerId").GetString());

            var finalVersion = success
                .GetProperty("rowVersion").GetString();

            Assert.NotEqual(initialVersion, finalVersion);

            // 直接查詢 SQL Server 最終資料
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .Include(request => request.ApprovalLog)
                    .SingleAsync(request => request.Id == requestId);

                Assert.Equal(expectedDecision, saved.Status);
                Assert.Equal(employeeId, saved.RequesterId);
                Assert.NotNull(saved.SubmittedAtUtc);
                Assert.Single(saved.Items);

                Assert.Equal(
                    2400m,
                    saved.Items.Sum(item =>
                        item.Quantity *
                        item.EstimatedUnitPrice));

                Assert.Equal(
                    finalVersion,
                    Convert.ToBase64String(saved.RowVersion));

                Assert.NotNull(saved.ApprovalLog);

                Assert.Equal(
                    expectedDecision,
                    saved.ApprovalLog!.Decision);

                Assert.Equal(
                    expectedComment,
                    saved.ApprovalLog.Comment);

                Assert.Equal(
                    expectedReviewerId,
                    saved.ApprovalLog.ReviewerId);

                Assert.Equal(
                    successLog.GetProperty("id").GetInt32(),
                    saved.ApprovalLog.Id);

                Assert.NotEqual(
                    default,
                    saved.ApprovalLog.ReviewedAtUtc);

                var logCount = await db.ApprovalLogs
                    .CountAsync(log =>
                        log.PurchaseRequestId == requestId);

                Assert.Equal(1, logCount);
            }
        }
        finally
        {
            // 清除本次測試資料
            using var scope = factory.Services.CreateScope();

            var db = scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>();

            EnsureTestDatabase(db);

            if (requestId > 0)
            {
                var logs = await db.ApprovalLogs
                    .Where(log =>
                        log.PurchaseRequestId == requestId)
                    .ToListAsync();

                db.ApprovalLogs.RemoveRange(logs);
                await db.SaveChangesAsync();

                var request = await db.PurchaseRequests
                    .SingleOrDefaultAsync(item =>
                        item.Id == requestId);

                if (request is not null)
                {
                    db.PurchaseRequests.Remove(request);
                    await db.SaveChangesAsync();
                }
            }

            var userManager = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();

            foreach (var userId in new[]
            {
                employeeId,
                managerAId,
                managerBId
            })
            {
                if (userId is null)
                {
                    continue;
                }

                var user = await userManager.FindByIdAsync(userId);

                if (user is not null)
                {
                    Assert.True(
                        (await userManager.DeleteAsync(user)).Succeeded);
                }
            }
        }
    }

    [Fact]
    public async Task Manager_Decision_WhenApprovalLogConflicts_RollsBack()
    {
        using var factory = new SqlServerTestApiFactory();

        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        var managerName = $"rollback-manager-{Guid.NewGuid():N}";
        var employeeName = $"rollback-employee-{Guid.NewGuid():N}";
        const string password = "TestPassword!4286";

        string? managerId = null;
        string? employeeId = null;

        int requestId = 0;
        int existingLogId = 0;
        string originalVersion = "";

        try
        {
            // 建立測試帳號及資料
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

                var pending = new PurchaseRequest
                {
                    RequesterId = employee.Id,
                    Title = "交易失敗回復測試",
                    Purpose = "驗證審核紀錄寫入失敗",
                    Status = "Pending",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    SubmittedAtUtc = now,
                    Items =
                    [
                        new PurchaseRequestItem
                        {
                            Name = "測試設備",
                            Quantity = 2,
                            EstimatedUnitPrice = 1000m
                        }
                    ]
                };

                db.PurchaseRequests.Add(pending);
                await db.SaveChangesAsync();

                requestId = pending.Id;

                // 刻意建立一筆已存在的審核紀錄，
                // 但保留主單為 Pending。
                // 這是專門用來觸發唯一索引衝突的測試資料。
                var existingLog = new ApprovalLog
                {
                    PurchaseRequestId = requestId,
                    ReviewerId = manager.Id,
                    Decision = "Rejected",
                    Comment = "測試前已存在的審核紀錄",
                    ReviewedAtUtc = now
                };

                db.ApprovalLogs.Add(existingLog);
                await db.SaveChangesAsync();

                existingLogId = existingLog.Id;

                originalVersion = Convert.ToBase64String(
                    pending.RowVersion);
            }

            // Manager 登入
            await LoginAsync(client, managerName, password);

            var csrf = await GetCsrfTokenAsync(client);

            // 嘗試核准 Pending。
            // 新增 ApprovalLog 時應觸發唯一索引衝突。
            using var response = await SendDecisionAsync(
                client,
                requestId,
                csrf,
                "Approved",
                "嘗試核准",
                originalVersion);

            Assert.Equal(
                HttpStatusCode.Conflict,
                response.StatusCode);

            // 重新建立 DbContext，直接檢查 SQL Server
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();

                EnsureTestDatabase(db);

                var saved = await db.PurchaseRequests
                    .AsNoTracking()
                    .Include(request => request.Items)
                    .Include(request => request.ApprovalLog)
                    .SingleAsync(request =>
                        request.Id == requestId);

                // 主單不得變成 Approved
                Assert.Equal("Pending", saved.Status);

                Assert.Equal(employeeId, saved.RequesterId);

                Assert.NotNull(saved.SubmittedAtUtc);

                Assert.Single(saved.Items);

                Assert.Equal(
                    2000m,
                    saved.Items.Sum(item =>
                        item.Quantity *
                        item.EstimatedUnitPrice));

                // 主單必須保留原版本
                Assert.Equal(
                    originalVersion,
                    Convert.ToBase64String(saved.RowVersion));

                // 原審核紀錄不能被覆蓋
                Assert.NotNull(saved.ApprovalLog);

                Assert.Equal(
                    existingLogId,
                    saved.ApprovalLog!.Id);

                Assert.Equal(
                    "Rejected",
                    saved.ApprovalLog.Decision);

                Assert.Equal(
                    "測試前已存在的審核紀錄",
                    saved.ApprovalLog.Comment);

                Assert.Equal(
                    managerId,
                    saved.ApprovalLog.ReviewerId);

                // 不得產生第二筆審核紀錄
                var logCount = await db.ApprovalLogs
                    .CountAsync(log =>
                        log.PurchaseRequestId == requestId);

                Assert.Equal(1, logCount);
            }
        }
        finally
        {
            // 清除本次測試資料
            using var scope = factory.Services.CreateScope();

            var db = scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>();

            EnsureTestDatabase(db);

            if (requestId > 0)
            {
                var logs = await db.ApprovalLogs
                    .Where(log =>
                        log.PurchaseRequestId == requestId)
                    .ToListAsync();

                db.ApprovalLogs.RemoveRange(logs);
                await db.SaveChangesAsync();

                var request = await db.PurchaseRequests
                    .SingleOrDefaultAsync(request =>
                        request.Id == requestId);

                if (request is not null)
                {
                    db.PurchaseRequests.Remove(request);
                    await db.SaveChangesAsync();
                }
            }

            var userManager = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();

            foreach (var userId in new[] { employeeId, managerId })
            {
                if (userId is null)
                {
                    continue;
                }

                var user = await userManager.FindByIdAsync(userId);

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
        var csrf = await GetCsrfTokenAsync(client);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new
            {
                userName,
                password
            })
        };

        request.Headers.Add("X-CSRF-TOKEN", csrf);

        using var response = await client.SendAsync(request);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendDecisionAsync(
        HttpClient client,
        int requestId,
        string csrfToken,
        string decision,
        string? comment,
        string rowVersion)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/purchase-requests/{requestId}/decision")
        {
            Content = JsonContent.Create(new
            {
                decision,
                comment,
                rowVersion
            })
        };

        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        return await client.SendAsync(request);
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
