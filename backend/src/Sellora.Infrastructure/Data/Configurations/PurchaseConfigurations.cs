using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

/// <summary>Shared constants and mapping of the TP-3 purchase tax snapshot columns (docs/features/tax-profiles.md, TP-3 design).</summary>
internal static class PurchaseTaxValues
{
    public const string PlaceOfSupplyOrNull = "\"PlaceOfSupply\" IS NULL OR \"PlaceOfSupply\" IN ('IntraState','InterState','NotApplicable')";

    /// <summary>TP-5a (PrepareTaxHistoryBackfill): a version never exists without its profile. History lines carry the backfilled profile with NO version (BR24: a version is never invented),
    /// so the TP-3/TP-4 "both or neither" pair constraint was relaxed to this one.</summary>
    public const string VersionNeedsProfile = "\"TaxProfileVersionId\" IS NULL OR \"TaxProfileId\" IS NOT NULL";

    /// <summary>Navigation-less FKs from a line to the profile and the version it was calculated with. The lines have no CompanyId, so these are simple FKs; "the version
    /// belongs to the profile and the company" is checked by V__tax-purchase-snapshot.sql. The migrations add them NOT VALID and validate in the next migration.</summary>
    public static void MapProfileLink<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> builder, string table, bool partialIndexes = false) where T : class
    {
        builder.HasOne<TaxProfile>().WithMany().HasForeignKey("TaxProfileId").IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName($"FK_{table}_TaxProfiles_TaxProfileId");
        builder.HasOne<TaxProfileVersion>().WithMany().HasForeignKey("TaxProfileVersionId").IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName($"FK_{table}_TaxProfileVersions_TaxProfileVersionId");
        // TP-4 (TP4-Q9): the big SalesInvoiceLines table gets partial indexes (WHERE ... IS NOT NULL) that the runbook pre-creates CONCURRENTLY.
        var profileIndex = builder.HasIndex("TaxProfileId").HasDatabaseName($"IX_{table}_TaxProfileId");
        var versionIndex = builder.HasIndex("TaxProfileVersionId").HasDatabaseName($"IX_{table}_TaxProfileVersionId");
        if (partialIndexes)
        {
            profileIndex.HasFilter("\"TaxProfileId\" IS NOT NULL");
            versionIndex.HasFilter("\"TaxProfileVersionId\" IS NOT NULL");
        }
    }
}

public class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.PurchaseNo).HasMaxLength(30).IsRequired();
        builder.Property(p => p.SupplierInvoiceNumber).HasMaxLength(60);
        builder.Property(p => p.Notes).HasMaxLength(500);
        builder.Property(p => p.PaymentMethod).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);

        foreach (var money in new[] { nameof(PurchaseOrder.PaidAmount), nameof(PurchaseOrder.ReturnedAmount), nameof(PurchaseOrder.Subtotal), nameof(PurchaseOrder.TaxTotal), nameof(PurchaseOrder.OtherChargesTotal), nameof(PurchaseOrder.DiscountTotal), nameof(PurchaseOrder.GrandTotal) })
        {
            builder.Property(money).HasColumnType("numeric(18,2)");
        }

        builder.Property(p => p.ExchangeRateToBase).HasColumnType("numeric(18,6)");
        builder.Property(p => p.OriginalGrandTotal).HasColumnType("numeric(18,4)");
        builder.Property(p => p.OriginalPaidAmount).HasColumnType("numeric(18,4)");
        builder.Property(p => p.OriginalReturnedAmount).HasColumnType("numeric(18,4)");

        // TP-3 tax snapshot: new header money columns are numeric(18,4) (TP3-Q2). DB defaults keep the previous build able to insert (constant defaults, no rewrite).
        builder.Property(p => p.RatesIncludeTax).HasDefaultValue(false);
        builder.Property(p => p.BillDiscountPercent).HasColumnType("numeric(9,4)");
        foreach (var money in new[] { nameof(PurchaseOrder.BillDiscountAmount), nameof(PurchaseOrder.BillCharges), nameof(PurchaseOrder.OriginalBillDiscountAmount), nameof(PurchaseOrder.OriginalBillCharges) })
        {
            builder.Property(money).HasColumnType("numeric(18,4)").HasDefaultValue(0m);
        }
        builder.Property(p => p.TaxableTotal).HasColumnType("numeric(18,4)");
        builder.Property(p => p.PlaceOfSupply).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.PartyTaxStatus).HasConversion<string>().HasMaxLength(20).HasDefaultValue(PartyTaxStatus.Unspecified);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PurchaseOrders_PlaceOfSupply", PurchaseTaxValues.PlaceOfSupplyOrNull);
            t.HasCheckConstraint("CK_PurchaseOrders_PartyTaxStatus", "\"PartyTaxStatus\" IN ('Unspecified')");
            t.HasCheckConstraint("CK_PurchaseOrders_BillDiscountPercent_Range", "\"BillDiscountPercent\" IS NULL OR (\"BillDiscountPercent\" >= 0 AND \"BillDiscountPercent\" <= 100)");
            t.HasCheckConstraint("CK_PurchaseOrders_BillAmounts_NonNegative",
                "\"BillDiscountAmount\" >= 0 AND \"BillCharges\" >= 0 AND \"OriginalBillDiscountAmount\" >= 0 AND \"OriginalBillCharges\" >= 0");
        });
        // States are global masters: simple FKs (the supplier's state and the state the supply was placed in). The migrations add them NOT VALID, then validate.
        builder.HasOne<State>().WithMany().HasForeignKey(p => p.PlaceOfSupplyStateId).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PurchaseOrders_States_PlaceOfSupplyStateId");
        builder.HasOne<State>().WithMany().HasForeignKey(p => p.SupplierStateId).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PurchaseOrders_States_SupplierStateId");
        builder.HasIndex(p => p.PlaceOfSupplyStateId, "IX_PurchaseOrders_PlaceOfSupplyStateId");
        builder.HasIndex(p => p.SupplierStateId, "IX_PurchaseOrders_SupplierStateId");

        builder.Ignore(p => p.NetTotal);
        builder.Ignore(p => p.OriginalNetTotal);
        builder.Ignore(p => p.OriginalRefundableAmount);
        builder.Ignore(p => p.OriginalDueAmount);

        builder.Ignore(p => p.DueAmount);
        builder.Ignore(p => p.PaymentStatus);

        builder.HasAlternateKey(p => new { p.CompanyId, p.Id });
        builder.HasIndex(p => new { p.CompanyId, p.PurchaseNo }, "IX_PurchaseOrders_CompanyId_PurchaseNo").IsUnique();
        builder.HasIndex(p => new { p.CompanyId, p.BranchId, p.PurchaseDate }, "IX_PurchaseOrders_CompanyId_BranchId_PurchaseDate");
        // Same-company branch: (CompanyId, BranchId) -> Branches (CompanyId, Id). Navigation-less (no filtered required navigation, Phase 1 section 8).
        builder.HasOne<Branch>().WithMany().HasForeignKey(p => new { p.CompanyId, p.BranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PurchaseOrders_Branches_CompanyId_BranchId");
        // Phase 8: the one location this document moves stock at. Same-company composite FK, navigation-less; FK index leads with CompanyId.
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(p => new { p.CompanyId, p.StockLocationId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PurchaseOrders_StockLocations_CompanyId_StockLocationId");
        builder.HasIndex(p => new { p.CompanyId, p.StockLocationId }, "IX_PurchaseOrders_CompanyId_StockLocationId");

        builder.HasOne(p => p.Supplier).WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.Restrict);
        // Same-company guarantee: the supplier belongs to the purchase's company.
        builder.HasOne<Supplier>().WithMany().HasForeignKey(p => new { p.CompanyId, p.SupplierId })
            .HasPrincipalKey(s => new { s.CompanyId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.Currency).WithMany().HasForeignKey(p => p.CurrencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchasePaymentConfiguration : IEntityTypeConfiguration<PurchasePayment>
{
    public void Configure(EntityTypeBuilder<PurchasePayment> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Amount).HasColumnType("numeric(18,4)");
        builder.Property(p => p.ExchangeRateToBase).HasColumnType("numeric(18,6)");
        builder.Property(p => p.BaseAmount).HasColumnType("numeric(18,2)");
        builder.Property(p => p.PaymentMethod).HasConversion<string>().HasMaxLength(20);

        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(10).HasDefaultValue(PaymentEntryKind.Payment);
        builder.Property(p => p.Reason).HasMaxLength(200);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PurchasePayments_Amount_Positive", "\"Amount\" > 0");
            // Only a reversal names the payment it undoes.
            t.HasCheckConstraint("CK_PurchasePayments_ReversalLink", "(\"Kind\" = 'Reversal') = (\"ReversesPaymentId\" IS NOT NULL)");
        });
        // A payment can be reversed at most once.
        builder.HasIndex(p => p.ReversesPaymentId).IsUnique();
        builder.HasIndex(p => p.PurchaseReturnId);
        builder.HasIndex(p => new { p.PurchaseOrderId, p.CreatedAt });

        builder.HasOne(p => p.ReversesPayment).WithMany().HasForeignKey(p => p.ReversesPaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.PurchaseReturn).WithMany().HasForeignKey(p => p.PurchaseReturnId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.PurchaseOrder).WithMany(o => o.Payments).HasForeignKey(p => p.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchaseReturnConfiguration : IEntityTypeConfiguration<PurchaseReturn>
{
    public void Configure(EntityTypeBuilder<PurchaseReturn> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ReturnNo).HasMaxLength(30).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(300);
        foreach (var money in new[] { nameof(PurchaseReturn.TaxableTotal), nameof(PurchaseReturn.TaxTotal), nameof(PurchaseReturn.Total) })
        {
            builder.Property(money).HasColumnType("numeric(18,2)");
        }
        builder.Property(r => r.OriginalTotal).HasColumnType("numeric(18,4)");

        builder.ToTable(t => t.HasCheckConstraint("CK_PurchaseReturns_Total_Positive", "\"Total\" >= 0"));
        builder.HasIndex(r => new { r.CompanyId, r.ReturnNo }, "IX_PurchaseReturns_CompanyId_ReturnNo").IsUnique();
        builder.HasIndex(r => new { r.CompanyId, r.BranchId, r.ReturnDate }, "IX_PurchaseReturns_CompanyId_BranchId_ReturnDate");
        // Same-company branch: (CompanyId, BranchId) -> Branches (CompanyId, Id). Navigation-less (no filtered required navigation, Phase 1 section 8).
        builder.HasOne<Branch>().WithMany().HasForeignKey(r => new { r.CompanyId, r.BranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PurchaseReturns_Branches_CompanyId_BranchId");
        // Phase 8: the one location this document moves stock at. Same-company composite FK, navigation-less; FK index leads with CompanyId.
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(r => new { r.CompanyId, r.StockLocationId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PurchaseReturns_StockLocations_CompanyId_StockLocationId");
        builder.HasIndex(r => new { r.CompanyId, r.StockLocationId }, "IX_PurchaseReturns_CompanyId_StockLocationId");
        builder.HasIndex(r => new { r.PurchaseOrderId, r.CreatedAt });

        builder.HasOne(r => r.PurchaseOrder).WithMany(o => o.Returns).HasForeignKey(r => r.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        // Same-company guarantee: a return and its purchase share the company.
        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(r => new { r.CompanyId, r.PurchaseOrderId })
            .HasPrincipalKey(o => new { o.CompanyId, o.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.CreatedByUser).WithMany().HasForeignKey(r => r.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchaseReturnLineConfiguration : IEntityTypeConfiguration<PurchaseReturnLine>
{
    public void Configure(EntityTypeBuilder<PurchaseReturnLine> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Qty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.ConversionFactorToBase).HasColumnType("numeric(28,10)");
        builder.Property(l => l.UnitCode).HasMaxLength(10).IsRequired();
        builder.Property(l => l.BaseQty).HasColumnType("numeric(18,3)");
        foreach (var money in new[] { nameof(PurchaseReturnLine.TaxableValue), nameof(PurchaseReturnLine.TaxAmount), nameof(PurchaseReturnLine.LineTotal), nameof(PurchaseReturnLine.CostRemoved) })
        {
            builder.Property(money).HasColumnType("numeric(18,4)");
        }

        builder.Property(l => l.PlaceOfSupply).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.OriginalTaxableValue).HasColumnType("numeric(18,4)");
        builder.Property(l => l.OriginalTaxAmount).HasColumnType("numeric(18,4)");
        PurchaseTaxValues.MapProfileLink<PurchaseReturnLine>(builder, "PurchaseReturnLines");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PurchaseReturnLines_PlaceOfSupply", PurchaseTaxValues.PlaceOfSupplyOrNull);
            t.HasCheckConstraint("CK_PurchaseReturnLines_TaxProfile_VersionNeedsProfile", PurchaseTaxValues.VersionNeedsProfile);
            t.HasCheckConstraint("CK_PurchaseReturnLines_Qty_Positive", "\"Qty\" > 0");
            t.HasCheckConstraint("CK_PurchaseReturnLines_Factor_Positive", "\"ConversionFactorToBase\" > 0");
            t.HasCheckConstraint("CK_PurchaseReturnLines_UnitCode_NotEmpty", "char_length(\"UnitCode\") > 0");
            t.HasCheckConstraint("CK_PurchaseReturnLines_BaseQty_Arithmetic", "\"Qty\" * \"ConversionFactorToBase\" = \"BaseQty\"");
        });
        builder.HasIndex(l => l.PurchaseOrderLineId);

        builder.HasOne(l => l.PurchaseReturn).WithMany(r => r.Lines).HasForeignKey(l => l.PurchaseReturnId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(l => l.PurchaseOrderLine).WithMany().HasForeignKey(l => l.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        // (ProductId, ProductUnitId) -> ProductUnits (ProductId, Id): the return unit must belong to the returned product.
        builder.HasOne(l => l.ProductUnit).WithMany()
            .HasForeignKey(l => new { l.ProductId, l.ProductUnitId })
            .HasPrincipalKey(u => new { u.ProductId, u.Id })
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Unit).WithMany().HasForeignKey(l => l.UnitId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Qty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.ConversionFactorToBase).HasColumnType("numeric(28,10)");
        builder.Property(l => l.UnitCode).HasMaxLength(10).IsRequired();
        builder.Property(l => l.BaseQty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.TaxRatePercent).HasColumnType("numeric(6,3)");

        foreach (var money in new[] { nameof(PurchaseOrderLine.Rate), nameof(PurchaseOrderLine.OtherCharges), nameof(PurchaseOrderLine.Discount), nameof(PurchaseOrderLine.TaxableValue), nameof(PurchaseOrderLine.TaxAmount), nameof(PurchaseOrderLine.LineTotal) })
        {
            builder.Property(money).HasColumnType("numeric(18,4)");
        }

        // Landed cost per BASE unit (M7: 6 dp).
        builder.Property(l => l.UnitCost).HasColumnType("numeric(18,6)");

        // TP-3 tax snapshot. DB defaults (false / 0) keep the previous build able to insert.
        builder.Property(l => l.PlaceOfSupply).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.HsnCode).HasMaxLength(8);
        builder.Property(l => l.PriceIncludesTax).HasDefaultValue(false);
        builder.Property(l => l.BillDiscountShare).HasColumnType("numeric(18,4)").HasDefaultValue(0m);
        builder.Property(l => l.BillChargeShare).HasColumnType("numeric(18,4)").HasDefaultValue(0m);
        foreach (var original in new[] { nameof(PurchaseOrderLine.OriginalRate), nameof(PurchaseOrderLine.OriginalDiscount), nameof(PurchaseOrderLine.OriginalOtherCharges),
                     nameof(PurchaseOrderLine.OriginalBillDiscountShare), nameof(PurchaseOrderLine.OriginalBillChargeShare), nameof(PurchaseOrderLine.OriginalTaxableValue),
                     nameof(PurchaseOrderLine.OriginalTaxAmount), nameof(PurchaseOrderLine.OriginalLineTotal) })
        {
            builder.Property(original).HasColumnType("numeric(18,4)");
        }
        PurchaseTaxValues.MapProfileLink<PurchaseOrderLine>(builder, "PurchaseOrderLines");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PurchaseOrderLines_PlaceOfSupply", PurchaseTaxValues.PlaceOfSupplyOrNull);
            t.HasCheckConstraint("CK_PurchaseOrderLines_TaxProfile_VersionNeedsProfile", PurchaseTaxValues.VersionNeedsProfile);
            t.HasCheckConstraint("CK_PurchaseOrderLines_HsnCode_Format", TaxValues.HsnFormat);
            t.HasCheckConstraint("CK_PurchaseOrderLines_BillShares_NonNegative", "\"BillDiscountShare\" >= 0 AND \"BillChargeShare\" >= 0");
            t.HasCheckConstraint("CK_PurchaseOrderLines_Qty_Positive", "\"Qty\" > 0");
            t.HasCheckConstraint("CK_PurchaseOrderLines_Factor_Positive", "\"ConversionFactorToBase\" > 0");
            t.HasCheckConstraint("CK_PurchaseOrderLines_UnitCode_NotEmpty", "char_length(\"UnitCode\") > 0");
            t.HasCheckConstraint("CK_PurchaseOrderLines_BaseQty_Arithmetic", "\"Qty\" * \"ConversionFactorToBase\" = \"BaseQty\"");
        });

        builder.HasOne(l => l.PurchaseOrder).WithMany(p => p.Lines).HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        // (ProductId, ProductUnitId) -> ProductUnits (ProductId, Id) via AK_ProductUnits_ProductId_Id: same-product guaranteed by the DB.
        builder.HasOne(l => l.ProductUnit).WithMany()
            .HasForeignKey(l => new { l.ProductId, l.ProductUnitId })
            .HasPrincipalKey(u => new { u.ProductId, u.Id })
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Unit).WithMany().HasForeignKey(l => l.UnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Tax).WithMany().HasForeignKey(l => l.TaxId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchaseBatchConfiguration : IEntityTypeConfiguration<PurchaseBatch>
{
    public void Configure(EntityTypeBuilder<PurchaseBatch> builder)
    {
        builder.HasKey(b => b.Id);
        builder.HasAlternateKey(b => new { b.CompanyId, b.Id });
        builder.Property(b => b.PurchasedBaseQty).HasColumnType("numeric(18,3)");
        builder.Property(b => b.RemainingBaseQty).HasColumnType("numeric(18,3)");
        builder.Property(b => b.UnitCost).HasColumnType("numeric(18,6)");

        builder.Property(b => b.BatchSequence).UseIdentityAlwaysColumn();
        builder.Ignore(b => b.InternalBatchNo);
        builder.Property(b => b.SupplierBatchNumber).HasMaxLength(60);
        builder.ToTable(t => t.HasCheckConstraint("CK_PurchaseBatches_RemainingWithinPurchased", "\"RemainingBaseQty\" >= 0 AND \"RemainingBaseQty\" <= \"PurchasedBaseQty\""));

        builder.HasOne(b => b.PurchaseOrderLine).WithOne(l => l.Batch).HasForeignKey<PurchaseBatch>(b => b.PurchaseOrderLineId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(b => b.StockAdjustment).WithOne(a => a.Batch).HasForeignKey<PurchaseBatch>(b => b.StockAdjustmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(b => b.Product).WithMany().HasForeignKey(b => b.ProductId).OnDelete(DeleteBehavior.Restrict);
        // Same-company guarantee: a batch and its product share the company.
        builder.HasOne<Product>().WithMany().HasForeignKey(b => new { b.CompanyId, b.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict);

        // Phase 8: receiving location. Explicit short name (the default would exceed 63 characters).
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(b => new { b.CompanyId, b.OriginStockLocationId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PurchaseBatches_StockLocations_Origin");
        builder.HasIndex(b => new { b.CompanyId, b.OriginStockLocationId }, "IX_PurchaseBatches_CompanyId_OriginStockLocationId");

        builder.HasIndex(b => new { b.ProductId, b.CreatedAt });
        builder.HasIndex(b => b.BatchSequence).IsUnique();
        // Deliberately non-unique: suppliers reuse lot numbers across deliveries.
        builder.HasIndex(b => b.SupplierBatchNumber);
    }
}

public class PurchaseOrderLineTaxConfiguration : IEntityTypeConfiguration<PurchaseOrderLineTax>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLineTax> builder)
    {
        LineTaxMapping.Apply(builder, "PurchaseOrderLineTaxes", "PurchaseOrderLineId");
        LineTaxMapping.ApplyPurchase(builder);
        builder.HasOne(r => r.Line).WithMany(l => l.Taxes).HasForeignKey(r => r.PurchaseOrderLineId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_PurchaseOrderLineTaxes_PurchaseOrderLines");
    }
}

public class PurchaseReturnLineTaxConfiguration : IEntityTypeConfiguration<PurchaseReturnLineTax>
{
    public void Configure(EntityTypeBuilder<PurchaseReturnLineTax> builder)
    {
        LineTaxMapping.Apply(builder, "PurchaseReturnLineTaxes", "PurchaseReturnLineId");
        LineTaxMapping.ApplyPurchase(builder);
        builder.HasOne(r => r.Line).WithMany(l => l.Taxes).HasForeignKey(r => r.PurchaseReturnLineId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_PurchaseReturnLineTaxes_PurchaseReturnLines");
    }
}
