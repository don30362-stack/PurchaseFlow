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
