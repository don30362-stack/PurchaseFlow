using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PurchaseFlow.Api.Authorization;
using PurchaseFlow.Api.Contracts.PurchaseRequests;
using PurchaseFlow.Api.Data;
using PurchaseFlow.Api.Data.Entities;

namespace PurchaseFlow.Api.Controllers;

[ApiController]
[Route("api/purchase-requests")]
[Authorize(Policy = AuthorizationPolicies.ManagerOnly)]
public class PurchaseRequestDecisionsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public PurchaseRequestDecisionsController(
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    [HttpPost("{id:int}/decision")]
    public async Task<IActionResult> Decide(
        int id,
        [FromBody] PurchaseRequestDecisionRequest input,
        CancellationToken cancellationToken)
    {
        var reviewerId = _userManager.GetUserId(User);

        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            return Unauthorized();
        }

        // 只有 Pending 可以核准或拒絕
        var entity = await _db.PurchaseRequests
            .Include(request => request.Items)
            .SingleOrDefaultAsync(
                request => request.Id == id,
                cancellationToken);

        if (entity is null)
        {
            return NotFound();
        }

        if (entity.Status != "Pending")
        {
            return Conflict(new
            {
                message = "只有 Pending 狀態可以審核。"
            });
        }

        // 驗證審核決定與備註
        if (input.Decision is not ("Approved" or "Rejected"))
        {
            ModelState.AddModelError(
                "decision",
                "Decision 必須是 Approved 或 Rejected。");
        }

        var comment = input.Comment?.Trim();

        if (input.Decision == "Rejected" &&
            string.IsNullOrEmpty(comment))
        {
            ModelState.AddModelError(
                "comment",
                "拒絕時必須填寫理由。");
        }

        if (comment?.Length > 500)
        {
            ModelState.AddModelError(
                "comment",
                "審核備註不可超過 500 字。");
        }

        if (string.IsNullOrEmpty(comment))
        {
            comment = null;
        }

        // 驗證客戶端 RowVersion
        byte[] clientVersion;

        try
        {
            clientVersion = Convert.FromBase64String(
                input.RowVersion ?? "");
        }
        catch (FormatException)
        {
            clientVersion = [];
        }

        if (clientVersion.Length != 8)
        {
            ModelState.AddModelError(
                "rowVersion",
                "RowVersion 必須是有效的 8 位元組 Base64 字串。");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        // 先檢查客戶端版本是否過期
        if (!clientVersion.SequenceEqual(entity.RowVersion))
        {
            return Conflict(new
            {
                message = "資料已更新，請重新載入後再審核。"
            });
        }

        // EF Core 更新時必須使用客戶端版本檢查
        _db.Entry(entity)
            .Property(request => request.RowVersion)
            .OriginalValue = clientVersion;

        var now = DateTimeOffset.UtcNow;

        entity.Status = input.Decision;
        entity.UpdatedAtUtc = now;

        // 確保主單 RowVersion 更新
        _db.Entry(entity)
            .Property(request => request.UpdatedAtUtc)
            .IsModified = true;

        var log = new ApprovalLog
        {
            PurchaseRequestId = entity.Id,
            ReviewerId = reviewerId,
            Decision = input.Decision,
            Comment = comment,
            ReviewedAtUtc = now
        };

        _db.ApprovalLogs.Add(log);

        try
        {
            // 單次 SaveChanges：狀態和審核紀錄一起提交
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                message = "發生並行審核衝突，請重新載入。"
            });
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is SqlException sqlEx &&
                  sqlEx.Number is 2601 or 2627 &&
                  sqlEx.Message.Contains(
                      "IX_ApprovalLogs_PurchaseRequestId",
                      StringComparison.OrdinalIgnoreCase))
        {
            // 同一張申請的唯一審核紀錄發生衝突
            return Conflict(new
            {
                message = "此申請已被審核，請重新載入。"
            });
        }

        return Ok(new
        {
            id = entity.Id,
            title = entity.Title,
            purpose = entity.Purpose,
            status = entity.Status,
            totalAmount = entity.Items.Sum(
                item => item.Quantity *
                        item.EstimatedUnitPrice),
            createdAtUtc = entity.CreatedAtUtc,
            updatedAtUtc = entity.UpdatedAtUtc,
            submittedAtUtc = entity.SubmittedAtUtc,
            rowVersion = Convert.ToBase64String(
                entity.RowVersion),

            items = entity.Items
                .OrderBy(item => item.Id)
                .Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    quantity = item.Quantity,
                    estimatedUnitPrice = item.EstimatedUnitPrice,
                    lineTotal = item.Quantity *
                                item.EstimatedUnitPrice
                }),

            approvalLog = new
            {
                id = log.Id,
                reviewerId = log.ReviewerId,
                decision = log.Decision,
                comment = log.Comment,
                reviewedAtUtc = log.ReviewedAtUtc
            }
        });
    }
}
