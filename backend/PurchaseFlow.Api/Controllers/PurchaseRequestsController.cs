using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PurchaseFlow.Api.Authorization;
using PurchaseFlow.Api.Contracts.PurchaseRequests;
using PurchaseFlow.Api.Data;
using PurchaseFlow.Api.Data.Entities;

namespace PurchaseFlow.Api.Controllers;

[ApiController]
[Route("api/purchase-requests")]
[Authorize(Policy = AuthorizationPolicies.EmployeeOnly)]
public class PurchaseRequestsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    private const decimal MaxUnitPrice = 9_999_999_999_999_999.99m;

    public PurchaseRequestsController(
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }


    [HttpGet]
    public async Task<IActionResult> GetMine(
        [FromQuery] string? view,
        CancellationToken cancellationToken)
    {
        if (view != "mine")
        {
            return BadRequest(new
            {
                message = "目前僅支援 view=mine。"
            });
        }

        var requesterId = _userManager.GetUserId(User);

        if (string.IsNullOrWhiteSpace(requesterId))
        {
            return Unauthorized();
        }

        var requests = await _db.PurchaseRequests
            .AsNoTracking()
            .Include(request => request.Items)
            .Where(request => request.RequesterId == requesterId)
            .OrderByDescending(request => request.CreatedAtUtc)
            .ThenByDescending(request => request.Id)
            .ToListAsync(cancellationToken);

        var response = requests.Select(request => new
        {
            id = request.Id,
            title = request.Title,
            purpose = request.Purpose,
            status = request.Status,
            totalAmount = request.Items.Sum(
                item => item.Quantity * item.EstimatedUnitPrice),
            createdAtUtc = request.CreatedAtUtc,
            updatedAtUtc = request.UpdatedAtUtc,
            submittedAtUtc = request.SubmittedAtUtc,
            rowVersion = Convert.ToBase64String(request.RowVersion)
        });

        return Ok(response);
    }


    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        var requesterId = _userManager.GetUserId(User);

        if (string.IsNullOrWhiteSpace(requesterId))
        {
            return Unauthorized();
        }

        var request = await _db.PurchaseRequests
            .AsNoTracking()
            .Include(request => request.Items)
            .Where(request =>
                request.Id == id &&
                request.RequesterId == requesterId)
            .SingleOrDefaultAsync(cancellationToken);

        if (request is null)
        {
            return NotFound();
        }

        return Ok(new
        {
            id = request.Id,
            title = request.Title,
            purpose = request.Purpose,
            status = request.Status,
            totalAmount = request.Items.Sum(
                item => item.Quantity * item.EstimatedUnitPrice),
            createdAtUtc = request.CreatedAtUtc,
            updatedAtUtc = request.UpdatedAtUtc,
            submittedAtUtc = request.SubmittedAtUtc,
            rowVersion = Convert.ToBase64String(request.RowVersion),
            items = request.Items
                .OrderBy(item => item.Id)
                .Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    quantity = item.Quantity,
                    estimatedUnitPrice = item.EstimatedUnitPrice,
                    lineTotal =
                        item.Quantity * item.EstimatedUnitPrice
                }),
            approvalLog = (object?)null
        });
    }


    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdatePurchaseRequestRequest input,
        CancellationToken cancellationToken)
    {
        var requesterId = _userManager.GetUserId(User);

        if (string.IsNullOrWhiteSpace(requesterId))
        {
            return Unauthorized();
        }

        // 只能取得自己的申請
        var entity = await _db.PurchaseRequests
            .Include(request => request.Items)
            .SingleOrDefaultAsync(
                request =>
                    request.Id == id &&
                    request.RequesterId == requesterId,
                cancellationToken);

        if (entity is null)
        {
            return NotFound();
        }

        if (entity.Status != "Draft")
        {
            return Conflict(new
            {
                message = "只有 Draft 狀態可以修改。"
            });
        }

        // 驗證標題與用途
        var title = input.Title?.Trim();
        var purpose = input.Purpose?.Trim();

        if (string.IsNullOrEmpty(title) || title.Length > 120)
        {
            ModelState.AddModelError(
                "title", "標題必須為 1～120 字。");
        }

        if (purpose?.Length > 1000)
        {
            ModelState.AddModelError(
                "purpose", "用途說明不可超過 1000 字。");
        }

        if (string.IsNullOrEmpty(purpose))
        {
            purpose = null;
        }

        // 驗證 RowVersion
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

        // 驗證完整品項清單
        var newItems = new List<PurchaseRequestItem>();

        if (input.Items is null)
        {
            ModelState.AddModelError(
                "items", "品項清單不可為 null。");
        }
        else if (input.Items.Count > 20)
        {
            ModelState.AddModelError(
                "items", "每張申請最多 20 個品項。");
        }
        else
        {
            for (var i = 0; i < input.Items.Count; i++)
            {
                var source = input.Items[i];

                if (source is null)
                {
                    ModelState.AddModelError(
                        $"items[{i}]", "品項不可為 null。");
                    continue;
                }

                var name = source.Name?.Trim();

                var nameValid =
                    !string.IsNullOrEmpty(name) &&
                    name.Length <= 150;

                var quantityValid =
                    source.Quantity is >= 1 and <= 999;

                var priceValid =
                    source.EstimatedUnitPrice > 0 &&
                    source.EstimatedUnitPrice <= MaxUnitPrice &&
                    decimal.Round(
                        source.EstimatedUnitPrice, 2) ==
                        source.EstimatedUnitPrice;

                if (!nameValid)
                {
                    ModelState.AddModelError(
                        $"items[{i}].name",
                        "品項名稱必須為 1～150 字。");
                }

                if (!quantityValid)
                {
                    ModelState.AddModelError(
                        $"items[{i}].quantity",
                        "數量必須為 1～999。");
                }

                if (!priceValid)
                {
                    ModelState.AddModelError(
                        $"items[{i}].estimatedUnitPrice",
                        "單價必須大於 0，最多兩位小數，且符合 decimal(18,2) 範圍。");
                }

                if (nameValid && quantityValid && priceValid)
                {
                    newItems.Add(new PurchaseRequestItem
                    {
                        Name = name!,
                        Quantity = source.Quantity,
                        EstimatedUnitPrice =
                            source.EstimatedUnitPrice
                    });
                }
            }
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        // 比對客戶端取得的版本
        if (!clientVersion.SequenceEqual(entity.RowVersion))
        {
            return Conflict(new
            {
                message = "資料已被修改，請重新載入後再試。"
            });
        }

        // EF Core 必須使用客戶端版本進行並行檢查
        _db.Entry(entity)
            .Property(request => request.RowVersion)
            .OriginalValue = clientVersion;

        // 更新主單
        entity.Title = title!;
        entity.Purpose = purpose;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;

        // 即使只修改品項，也強制更新主單 RowVersion
        _db.Entry(entity)
            .Property(request => request.UpdatedAtUtc)
            .IsModified = true;

        // PUT：完整取代原本的品項清單
        var oldItems = entity.Items.ToList();

        _db.RemoveRange(oldItems);
        entity.Items.Clear();

        foreach (var item in newItems)
        {
            entity.Items.Add(item);
        }

        try
        {
            // 單次 SaveChanges，確保主單與品項一起成功或失敗
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                message = "資料發生並行衝突，請重新載入後再試。"
            });
        }

        return Ok(new
        {
            id = entity.Id,
            title = entity.Title,
            purpose = entity.Purpose,
            status = entity.Status,
            totalAmount = entity.Items.Sum(
                item => item.Quantity * item.EstimatedUnitPrice),
            createdAtUtc = entity.CreatedAtUtc,
            updatedAtUtc = entity.UpdatedAtUtc,
            submittedAtUtc = entity.SubmittedAtUtc,
            rowVersion = Convert.ToBase64String(entity.RowVersion),
            items = entity.Items
                .OrderBy(item => item.Id)
                .Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    quantity = item.Quantity,
                    estimatedUnitPrice = item.EstimatedUnitPrice,
                    lineTotal =
                        item.Quantity * item.EstimatedUnitPrice
                }),
            approvalLog = (object?)null
        });
    }

    [HttpPost("{id:int}/submit")]
    public async Task<IActionResult> Submit(
        int id,
        [FromBody] SubmitPurchaseRequestRequest input,
        CancellationToken cancellationToken)
    {
        var requesterId = _userManager.GetUserId(User);

        if (string.IsNullOrWhiteSpace(requesterId))
        {
            return Unauthorized();
        }

        // 只查詢登入者自己的申請
        var entity = await _db.PurchaseRequests
            .Include(request => request.Items)
            .SingleOrDefaultAsync(
                request =>
                    request.Id == id &&
                    request.RequesterId == requesterId,
                cancellationToken);

        if (entity is null)
        {
            return NotFound();
        }

        // 只有 Draft 可以送審
        if (entity.Status != "Draft")
        {
            return Conflict(new
            {
                message = "只有 Draft 狀態可以送出審核。"
            });
        }

        // 驗證 RowVersion 格式
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

        // 重新驗證草稿內容
        if (string.IsNullOrWhiteSpace(entity.Title) ||
            entity.Title.Trim().Length > 120)
        {
            ModelState.AddModelError(
                "title", "申請標題不合法。");
        }

        if (entity.Purpose?.Length > 1000)
        {
            ModelState.AddModelError(
                "purpose", "用途說明不可超過 1000 字。");
        }

        if (entity.Items.Count is < 1 or > 20)
        {
            ModelState.AddModelError(
                "items", "送審必須有 1～20 個有效品項。");
        }

        var invalidItems = entity.Items.Any(item =>
            string.IsNullOrWhiteSpace(item.Name) ||
            item.Name.Trim().Length > 150 ||
            item.Quantity is < 1 or > 999 ||
            item.EstimatedUnitPrice <= 0 ||
            item.EstimatedUnitPrice > MaxUnitPrice ||
            decimal.Round(
                item.EstimatedUnitPrice, 2) !=
                item.EstimatedUnitPrice);

        if (invalidItems)
        {
            ModelState.AddModelError(
                "items", "品項資料不符合送審規則。");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        // 比對客戶端上次取得的版本
        if (!clientVersion.SequenceEqual(entity.RowVersion))
        {
            return Conflict(new
            {
                message = "資料已被修改，請重新載入後再送審。"
            });
        }

        // 讓 EF Core 使用客戶端版本檢查並行衝突
        _db.Entry(entity)
            .Property(request => request.RowVersion)
            .OriginalValue = clientVersion;

        // Draft -> Pending
        var now = DateTimeOffset.UtcNow;

        entity.Status = "Pending";
        entity.SubmittedAtUtc = now;
        entity.UpdatedAtUtc = now;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                message = "送審發生並行衝突，請重新載入後再試。"
            });
        }

        // 成功後回傳更新後的申請明細
        return Ok(new
        {
            id = entity.Id,
            title = entity.Title,
            purpose = entity.Purpose,
            status = entity.Status,
            totalAmount = entity.Items.Sum(
                item => item.Quantity * item.EstimatedUnitPrice),
            createdAtUtc = entity.CreatedAtUtc,
            updatedAtUtc = entity.UpdatedAtUtc,
            submittedAtUtc = entity.SubmittedAtUtc,
            rowVersion = Convert.ToBase64String(entity.RowVersion),
            items = entity.Items
                .OrderBy(item => item.Id)
                .Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    quantity = item.Quantity,
                    estimatedUnitPrice = item.EstimatedUnitPrice,
                    lineTotal =
                        item.Quantity * item.EstimatedUnitPrice
                }),
            approvalLog = (object?)null
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreatePurchaseRequestRequest request,
        CancellationToken cancellationToken)
    {
        var requesterId = _userManager.GetUserId(User);

        if (string.IsNullOrWhiteSpace(requesterId))
        {
            return Unauthorized();
        }

        // 驗證標題與用途
        var title = request.Title?.Trim();
        var purpose = request.Purpose?.Trim();

        if (string.IsNullOrEmpty(title) || title.Length > 120)
        {
            ModelState.AddModelError(
                "title", "標題必須為 1～120 字。");
        }

        if (purpose?.Length > 1000)
        {
            ModelState.AddModelError(
                "purpose", "用途說明不可超過 1000 字。");
        }

        if (string.IsNullOrEmpty(purpose))
        {
            purpose = null;
        }

        var items = new List<PurchaseRequestItem>();

        // 驗證品項清單
        if (request.Items is null)
        {
            ModelState.AddModelError(
                "items", "品項清單不可為 null。");
        }
        else if (request.Items.Count > 20)
        {
            ModelState.AddModelError(
                "items", "每張申請最多 20 個品項。");
        }
        else
        {
            for (var i = 0; i < request.Items.Count; i++)
            {
                var source = request.Items[i];

                if (source is null)
                {
                    ModelState.AddModelError(
                        $"items[{i}]", "品項不可為 null。");
                    continue;
                }

                var name = source.Name?.Trim();

                var nameValid =
                    !string.IsNullOrEmpty(name) &&
                    name.Length <= 150;

                var quantityValid =
                    source.Quantity is >= 1 and <= 999;

                var priceValid =
                    source.EstimatedUnitPrice > 0 &&
                    source.EstimatedUnitPrice <= MaxUnitPrice &&
                    decimal.Round(
                        source.EstimatedUnitPrice, 2) ==
                        source.EstimatedUnitPrice;

                if (!nameValid)
                {
                    ModelState.AddModelError(
                        $"items[{i}].name",
                        "品項名稱必須為 1～150 字。");
                }

                if (!quantityValid)
                {
                    ModelState.AddModelError(
                        $"items[{i}].quantity",
                        "數量必須為 1～999。");
                }

                if (!priceValid)
                {
                    ModelState.AddModelError(
                        $"items[{i}].estimatedUnitPrice",
                        "單價必須大於 0，且最多兩位小數，並符合 decimal(18,2) 範圍。");
                }

                if (nameValid && quantityValid && priceValid)
                {
                    items.Add(new PurchaseRequestItem
                    {
                        Name = name!,
                        Quantity = source.Quantity,
                        EstimatedUnitPrice = source.EstimatedUnitPrice
                    });
                }
            }
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var now = DateTimeOffset.UtcNow;

        var entity = new PurchaseRequest
        {
            RequesterId = requesterId,
            Title = title!,
            Purpose = purpose,
            Status = "Draft",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SubmittedAtUtc = null,
            Items = items
        };

        _db.PurchaseRequests.Add(entity);

        // 同一次 SaveChanges 儲存主單與明細
        await _db.SaveChangesAsync(cancellationToken);

        var response = new
        {
            id = entity.Id,
            title = entity.Title,
            purpose = entity.Purpose,
            status = entity.Status,
            totalAmount = entity.Items.Sum(
                item => item.Quantity * item.EstimatedUnitPrice),
            rowVersion = Convert.ToBase64String(entity.RowVersion),
            items = entity.Items.Select(item => new
            {
                id = item.Id,
                name = item.Name,
                quantity = item.Quantity,
                estimatedUnitPrice = item.EstimatedUnitPrice,
                lineTotal = item.Quantity * item.EstimatedUnitPrice
            }),
            approvalLog = (object?)null
        };

        return Created(
            $"/api/purchase-requests/{entity.Id}",
            response);
    }
}
