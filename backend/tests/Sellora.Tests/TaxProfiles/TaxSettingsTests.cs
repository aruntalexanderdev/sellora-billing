using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sellora.Api.Contracts;
using Sellora.Api.Controllers;
using Sellora.Domain.Entities;
using Sellora.Infrastructure.Services;
using Sellora.Tests.Branches;

namespace Sellora.Tests.TaxProfiles;

/// <summary>T2, T15, T18: the price basis and the whole-bill discount cap travel through the business settings endpoint.</summary>
public class TaxSettingsTests
{
    private static async Task<(SettingsController Controller, Guid CurrencyId, Sellora.Infrastructure.Data.SelloraDbContext Db)> CreateAsync()
    {
        var w = await BranchWorld.CreateAsync();
        var db = w.Db();
        db.Companies.Add(new Company { Id = Sellora.Domain.Companies.DefaultTenant.CompanyId, Code = "DEF", Name = "Sellora Mart" });
        await db.SaveChangesAsync();
        var controller = new SettingsController(db, new BranchService(db, new StockLocationUsageService(db)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, w.UserId.ToString())], "test")) },
            },
        };
        return (controller, w.CurrencyId, db);
    }

    private static BusinessSettingsDto Dto(IActionResult? r) => Assert.IsType<BusinessSettingsDto>(Assert.IsType<OkObjectResult>(r).Value);

    [Fact]
    public async Task SettingsPut_SetsThePriceBasisAndTheDiscountCap_OmittedBasisStays_NullCapClears()
    {
        var (controller, currency, db) = await CreateAsync();
        var prefix = (await db.BusinessSettings.SingleAsync()).InvoicePrefix;
        UpdateBusinessSettingsRequest Request(bool? includeTax, decimal? cap) =>
            new("Sellora Mart", null, null, null, null, null, null, prefix, currency, SellingPricesIncludeTax: includeTax, MaxBillDiscountPercent: cap);

        var set = Dto((await controller.Update(Request(false, 10m), CancellationToken.None)).Result);
        Assert.False(set.SellingPricesIncludeTax);
        Assert.Equal(10m, set.MaxBillDiscountPercent);

        var kept = Dto((await controller.Update(Request(null, 7.555m), CancellationToken.None)).Result);
        Assert.False(kept.SellingPricesIncludeTax); // omitted: unchanged
        Assert.Equal(7.56m, kept.MaxBillDiscountPercent); // numeric(5,2)

        var cleared = Dto((await controller.Update(Request(true, null), CancellationToken.None)).Result);
        Assert.True(cleared.SellingPricesIncludeTax);
        Assert.Null(cleared.MaxBillDiscountPercent); // no limit

        var read = Dto((await controller.Get(CancellationToken.None)).Result);
        Assert.Equal((true, (decimal?)null), (read.SellingPricesIncludeTax, read.MaxBillDiscountPercent));
    }

    [Theory]
    [InlineData(-0.01, false)]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(100.01, false)]
    public void UpdateRequest_MaxBillDiscountPercent_MustBeBetween0And100(double value, bool valid)
    {
        // The attribute sits on the record's constructor parameter (what MVC validates for a bound record).
        var parameter = typeof(UpdateBusinessSettingsRequest).GetConstructors().Single().GetParameters().Single(p => p.Name == nameof(UpdateBusinessSettingsRequest.MaxBillDiscountPercent));
        var attributes = parameter.GetCustomAttributes(typeof(ValidationAttribute), inherit: true).Cast<ValidationAttribute>().ToList();

        Assert.NotEmpty(attributes);
        Assert.Equal(valid, Validator.TryValidateValue((decimal)value, new ValidationContext(new object()) { MemberName = "MaxBillDiscountPercent" }, new List<ValidationResult>(), attributes));
    }
}
