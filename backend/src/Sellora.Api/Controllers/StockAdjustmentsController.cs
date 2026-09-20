using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Api.Validation;
using Sellora.Domain.Abstractions;
using Sellora.Domain.Stock;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/stock-adjustments")]
[Authorize(Policy = "Inventory.Adjust")]
public class StockAdjustmentsController(IStockAdjustmentService adjustmentService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<StockAdjustmentDto>> Create(CreateStockAdjustmentRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!IdempotencyHeader.TryRead(Request, out var key)) return IdempotencyHeader.Invalid(this);

        var input = new CreateAdjustmentInput(
            request.ProductId!.Value, request.Type, request.Direction, request.QuantityBaseUnits, request.UnitCost, request.Reason, key);

        try
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
            var adjustment = await adjustmentService.CreateAdjustmentAsync(input, userId, ct);

            return Ok(new StockAdjustmentDto(
                adjustment.Id, adjustment.AdjustmentNo, adjustment.ProductId, adjustment.Product.Name,
                adjustment.Type, adjustment.Direction, adjustment.QuantityBaseUnits, adjustment.UnitCost,
                adjustment.Reason, adjustment.BeforeQty, adjustment.AfterQty,
                $"{adjustment.User.FirstName} {adjustment.User.LastName}", adjustment.CreatedAt));
        }
        catch (DomainValidationException ex)
        {
            ModelState.AddModelError(ex.Field, ex.Message);
            return ValidationProblem(ModelState);
        }
    }
}
