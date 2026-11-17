using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

// Phase 9 (stock transfers). Every relationship is a same-company composite FK, Restrict, navigation-less towards the shared masters
// (locations, branches, users, products, units); explicit names keep every identifier under 63 bytes.

public class StockTransferConfiguration : IEntityTypeConfiguration<StockTransfer>
{
    private const string OpenFilter = "\"Status\" NOT IN ('Completed', 'Cancelled', 'Rejected', 'Fulfilled', 'Closed')";

    public void Configure(EntityTypeBuilder<StockTransfer> builder)
    {
        builder.HasKey(t => t.Id);
        builder.HasAlternateKey(t => new { t.CompanyId, t.Id });
        builder.Property(t => t.TransferNo).HasMaxLength(30).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Notes).HasMaxLength(300);
        builder.Property(t => t.RejectionReason).HasMaxLength(300);
        builder.Property(t => t.CancellationReason).HasMaxLength(300);
        builder.Property(t => t.ClosureReason).HasMaxLength(500);

        builder.HasIndex(t => new { t.CompanyId, t.TransferNo }, "IX_StockTransfers_CompanyId_TransferNo").IsUnique();
        builder.HasIndex(t => new { t.CompanyId, t.Status }, "IX_StockTransfers_CompanyId_Status");
        builder.HasIndex(t => new { t.CompanyId, t.BranchId, t.CreatedAt }, "IX_StockTransfers_CompanyId_BranchId_CreatedAt");
        // Usage check (HasOpenTransfers): open transfers touching a location.
        builder.HasIndex(t => new { t.CompanyId, t.SourceLocationId }, "IX_StockTransfers_Source_Open").HasFilter(OpenFilter);
        builder.HasIndex(t => new { t.CompanyId, t.DestinationLocationId }, "IX_StockTransfers_Destination_Open").HasFilter(OpenFilter);
        // Branch filter (D7).
        builder.HasIndex(t => new { t.CompanyId, t.SourceBranchId }, "IX_StockTransfers_CompanyId_SourceBranchId").HasFilter("\"SourceBranchId\" IS NOT NULL");
        builder.HasIndex(t => new { t.CompanyId, t.DestinationBranchId }, "IX_StockTransfers_CompanyId_DestinationBranchId").HasFilter("\"DestinationBranchId\" IS NOT NULL");

        builder.HasOne<Branch>().WithMany().HasForeignKey(t => new { t.CompanyId, t.BranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransfers_Branches_BranchId");
        // Optional branch snapshots: MATCH SIMPLE skips the check while the branch id is NULL.
        builder.HasOne<Branch>().WithMany().HasForeignKey(t => new { t.CompanyId, t.SourceBranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransfers_Branches_SourceBranchId");
        builder.HasOne<Branch>().WithMany().HasForeignKey(t => new { t.CompanyId, t.DestinationBranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransfers_Branches_DestinationBranchId");

        builder.HasOne<StockLocation>().WithMany().HasForeignKey(t => new { t.CompanyId, t.SourceLocationId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransfers_StockLocations_Source");
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(t => new { t.CompanyId, t.DestinationLocationId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransfers_StockLocations_Destination");
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(t => new { t.CompanyId, t.TransitLocationId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransfers_StockLocations_Transit");

        builder.HasOne<User>().WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransfers_Users_CreatedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.SubmittedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransfers_Users_SubmittedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransfers_Users_ApprovedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.RejectedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransfers_Users_RejectedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.DispatchedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransfers_Users_DispatchedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.CompletedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransfers_Users_CompletedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.CancelledByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransfers_Users_CancelledBy");
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.ClosedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransfers_Users_ClosedBy");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StockTransfers_Endpoints_Differ", "\"SourceLocationId\" <> \"DestinationLocationId\"");
            t.HasCheckConstraint("CK_StockTransfers_Endpoints_NotTransit", "\"SourceLocationId\" <> \"TransitLocationId\" AND \"DestinationLocationId\" <> \"TransitLocationId\"");
            t.HasCheckConstraint("CK_StockTransfers_Status_Valid",
                "\"Status\" IN ('Draft', 'Requested', 'Approved', 'Rejected', 'Dispatched', 'PartiallyReceived', 'Completed', 'Cancelled', 'PartiallyFulfilled', 'Fulfilled', 'Closed')");
            // Each actor/time pair is all-or-none.
            t.HasCheckConstraint("CK_StockTransfers_Actors_Paired",
                "(\"SubmittedAt\" IS NULL) = (\"SubmittedByUserId\" IS NULL) AND (\"ApprovedAt\" IS NULL) = (\"ApprovedByUserId\" IS NULL) AND (\"RejectedAt\" IS NULL) = (\"RejectedByUserId\" IS NULL)"
                + " AND (\"DispatchedAt\" IS NULL) = (\"DispatchedByUserId\" IS NULL) AND (\"CompletedAt\" IS NULL) = (\"CompletedByUserId\" IS NULL) AND (\"CancelledAt\" IS NULL) = (\"CancelledByUserId\" IS NULL)"
                + " AND (\"ClosedAt\" IS NULL) = (\"ClosedByUserId\" IS NULL)");
            // Status-consistent actor columns (D1): a status needs its own stamps; a stamp needs its status. Draft may be cancelled without ever being submitted.
            t.HasCheckConstraint("CK_StockTransfers_Status_Stamps",
                "(\"Status\" = 'Draft') = (\"SubmittedAt\" IS NULL) OR \"Status\" = 'Cancelled'");
            t.HasCheckConstraint("CK_StockTransfers_Status_Approval",
                "(\"Status\" NOT IN ('Approved', 'Dispatched', 'PartiallyReceived', 'Completed', 'PartiallyFulfilled', 'Fulfilled', 'Closed') OR \"ApprovedAt\" IS NOT NULL)"
                + " AND (\"Status\" IN ('Approved', 'Dispatched', 'PartiallyReceived', 'Completed', 'PartiallyFulfilled', 'Fulfilled', 'Closed', 'Cancelled') OR \"ApprovedAt\" IS NULL)");
            t.HasCheckConstraint("CK_StockTransfers_Status_Rejection",
                "(\"Status\" = 'Rejected') = (\"RejectedAt\" IS NOT NULL) AND (\"Status\" <> 'Rejected' OR char_length(\"RejectionReason\") > 0)");
            t.HasCheckConstraint("CK_StockTransfers_Status_Dispatch",
                "(\"Status\" IN ('Dispatched', 'PartiallyReceived', 'Completed')) = (\"DispatchedAt\" IS NOT NULL)");
            t.HasCheckConstraint("CK_StockTransfers_Status_Completion", "(\"Status\" = 'Completed') = (\"CompletedAt\" IS NOT NULL)");
            // FI-D26: Closed <=> ClosedAt (actor paired above), with a mandatory reason; the reason exists only on a closed transfer.
            t.HasCheckConstraint("CK_StockTransfers_Status_Closure",
                "(\"Status\" = 'Closed') = (\"ClosedAt\" IS NOT NULL) AND (\"Status\" <> 'Closed' OR COALESCE(char_length(\"ClosureReason\"), 0) > 0) AND (\"Status\" = 'Closed' OR \"ClosureReason\" IS NULL)");
            t.HasCheckConstraint("CK_StockTransfers_Status_Cancellation",
                "(\"Status\" = 'Cancelled') = (\"CancelledAt\" IS NOT NULL) AND (\"Status\" <> 'Cancelled' OR char_length(\"CancellationReason\") > 0)");
        });
    }
}

public class StockTransferLineConfiguration : IEntityTypeConfiguration<StockTransferLine>
{
    public void Configure(EntityTypeBuilder<StockTransferLine> builder)
    {
        builder.HasKey(l => l.Id);
        builder.HasAlternateKey(l => new { l.CompanyId, l.Id });
        builder.Property(l => l.UnitCode).HasMaxLength(10).IsRequired();
        builder.Property(l => l.DispatchUnitCode).HasMaxLength(10);
        builder.Property(l => l.ConversionFactorToBase).HasColumnType("numeric(28,10)");
        builder.Property(l => l.DispatchFactor).HasColumnType("numeric(28,10)");
        foreach (var p in new[] { nameof(StockTransferLine.RequestedQty), nameof(StockTransferLine.RequestedBaseQty), nameof(StockTransferLine.DispatchedQty),
                     nameof(StockTransferLine.DispatchedBaseQty), nameof(StockTransferLine.AllocatedBaseQty), nameof(StockTransferLine.ClosedBaseQty), nameof(StockTransferLine.ReceivedBaseQty), nameof(StockTransferLine.DamagedBaseQty),
                     nameof(StockTransferLine.WrittenOffBaseQty), nameof(StockTransferLine.ReturnedBaseQty) })
        {
            builder.Property(p).HasColumnType("numeric(18,3)");
        }
        builder.Property(l => l.AllocatedBaseQty).HasDefaultValue(0m);
        builder.Property(l => l.ClosedBaseQty).HasDefaultValue(0m);

        builder.HasIndex(l => new { l.CompanyId, l.TransferId, l.LineNo }, "IX_StockTransferLines_CompanyId_TransferId_LineNo").IsUnique();
        // ProductUnit immutability / usage lookups.
        builder.HasIndex(l => new { l.ProductId, l.ProductUnitId }, "IX_StockTransferLines_ProductId_ProductUnitId");
        builder.HasIndex(l => new { l.ProductId, l.DispatchProductUnitId }, "IX_StockTransferLines_ProductId_DispatchProductUnitId")
            .HasFilter("\"DispatchProductUnitId\" IS NOT NULL");

        builder.HasOne<StockTransfer>().WithMany(t => t.Lines).HasForeignKey(l => new { l.CompanyId, l.TransferId })
            .HasPrincipalKey(t => new { t.CompanyId, t.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferLines_StockTransfers");
        builder.HasOne<Product>().WithMany().HasForeignKey(l => new { l.CompanyId, l.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferLines_Products");
        // Same-product: (ProductId, unit id) -> ProductUnits (ProductId, Id).
        builder.HasOne<ProductUnit>().WithMany().HasForeignKey(l => new { l.ProductId, l.ProductUnitId })
            .HasPrincipalKey(u => new { u.ProductId, u.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferLines_ProductUnits_Request");
        builder.HasOne<ProductUnit>().WithMany().HasForeignKey(l => new { l.ProductId, l.DispatchProductUnitId })
            .HasPrincipalKey(u => new { u.ProductId, u.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferLines_ProductUnits_Dispatch");
        builder.HasOne<Unit>().WithMany().HasForeignKey(l => l.UnitId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransferLines_Units_Request");
        builder.HasOne<Unit>().WithMany().HasForeignKey(l => l.DispatchUnitId).IsRequired(false).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransferLines_Units_Dispatch");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StockTransferLines_LineNo_Positive", "\"LineNo\" > 0");
            t.HasCheckConstraint("CK_StockTransferLines_Requested_Positive", "\"RequestedQty\" > 0 AND \"ConversionFactorToBase\" > 0 AND \"RequestedBaseQty\" > 0");
            t.HasCheckConstraint("CK_StockTransferLines_Requested_Arithmetic", "\"RequestedQty\" * \"ConversionFactorToBase\" = \"RequestedBaseQty\"");
            // Dispatch snapshot is all-or-none; a line with no snapshot has nothing dispatched or settled.
            t.HasCheckConstraint("CK_StockTransferLines_Dispatch_AllOrNone",
                "(\"DispatchProductUnitId\" IS NULL) = (\"DispatchUnitId\" IS NULL) AND (\"DispatchProductUnitId\" IS NULL) = (\"DispatchUnitCode\" IS NULL)"
                + " AND (\"DispatchProductUnitId\" IS NULL) = (\"DispatchFactor\" IS NULL) AND (\"DispatchProductUnitId\" IS NULL) = (\"DispatchedQty\" IS NULL)");
            t.HasCheckConstraint("CK_StockTransferLines_Dispatch_Arithmetic",
                "\"DispatchedQty\" IS NULL OR (\"DispatchedQty\" >= 0 AND \"DispatchFactor\" > 0 AND \"DispatchedQty\" * \"DispatchFactor\" = \"DispatchedBaseQty\")");
            t.HasCheckConstraint("CK_StockTransferLines_Dispatch_Required", "\"DispatchProductUnitId\" IS NOT NULL OR (\"DispatchedBaseQty\" = 0 AND \"ReceivedBaseQty\" = 0 AND \"DamagedBaseQty\" = 0 AND \"WrittenOffBaseQty\" = 0 AND \"ReturnedBaseQty\" = 0)");
            t.HasCheckConstraint("CK_StockTransferLines_Dispatched_Max", "\"DispatchedBaseQty\" >= 0 AND \"DispatchedBaseQty\" + \"ClosedBaseQty\" <= \"RequestedBaseQty\"");
            t.HasCheckConstraint("CK_StockTransferLines_Allocation_Valid", "\"AllocatedBaseQty\" >= 0 AND \"ClosedBaseQty\" >= 0 AND \"AllocatedBaseQty\" + \"ClosedBaseQty\" <= \"RequestedBaseQty\"");
            t.HasCheckConstraint("CK_StockTransferLines_Counters_NonNegative", "\"ReceivedBaseQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"WrittenOffBaseQty\" >= 0 AND \"ReturnedBaseQty\" >= 0");
            t.HasCheckConstraint("CK_StockTransferLines_Counters_Max",
                "\"ReceivedBaseQty\" + \"DamagedBaseQty\" + \"WrittenOffBaseQty\" + \"ReturnedBaseQty\" <= \"DispatchedBaseQty\"");
        });
    }
}

public class StockTransferLineBatchConfiguration : IEntityTypeConfiguration<StockTransferLineBatch>
{
    public void Configure(EntityTypeBuilder<StockTransferLineBatch> builder)
    {
        builder.HasKey(b => b.Id);
        // One row per batch per request line for legacy direct dispatch; one per batch per consignment line for shipment dispatch (FI-D34).
        builder.HasIndex(b => new { b.LineId, b.PurchaseBatchId }, "IX_StockTransferLineBatches_LineId_PurchaseBatchId_Legacy").IsUnique().HasFilter("\"ConsignmentLineId\" IS NULL");
        builder.HasIndex(b => new { b.ConsignmentLineId, b.PurchaseBatchId }, "IX_StockTransferLineBatches_ConsignmentLineId_PurchaseBatchId").IsUnique().HasFilter("\"ConsignmentLineId\" IS NOT NULL");
        builder.Property(b => b.UnitCost).HasColumnType("numeric(18,6)");
        foreach (var p in new[] { nameof(StockTransferLineBatch.DispatchedBaseQty), nameof(StockTransferLineBatch.ReceivedBaseQty), nameof(StockTransferLineBatch.DamagedBaseQty),
                     nameof(StockTransferLineBatch.WrittenOffBaseQty), nameof(StockTransferLineBatch.ReturnedBaseQty) })
        {
            builder.Property(p).HasColumnType("numeric(18,3)");
        }
        builder.Property(b => b.AcceptedCorrectionBaseQty).HasColumnType("numeric(18,3)").HasDefaultValue(0m);

        builder.HasIndex(b => b.PurchaseBatchId, "IX_StockTransferLineBatches_PurchaseBatchId");
        builder.HasIndex(b => new { b.CompanyId, b.ConsignmentLineId }, "IX_StockTransferLineBatches_CompanyId_ConsignmentLineId").HasFilter("\"ConsignmentLineId\" IS NOT NULL");
        builder.HasIndex(b => new { b.CompanyId, b.LineId }, "IX_StockTransferLineBatches_CompanyId_LineId");

        builder.HasOne<StockTransferLine>().WithMany(l => l.Batches).HasForeignKey(b => new { b.CompanyId, b.LineId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferLineBatches_Lines");
        builder.HasOne<PurchaseBatch>().WithMany().HasForeignKey(b => new { b.CompanyId, b.PurchaseBatchId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferLineBatches_PurchaseBatches");
        // MATCH SIMPLE: legacy direct dispatch (NULL ConsignmentLineId) skips the check.
        builder.HasOne<ConsignmentLine>().WithMany().HasForeignKey(b => new { b.CompanyId, b.ConsignmentLineId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferLineBatches_ConsignmentLines");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StockTransferLineBatches_Dispatched_Positive", "\"DispatchedBaseQty\" > 0 AND \"UnitCost\" >= 0");
            t.HasCheckConstraint("CK_StockTransferLineBatches_Counters_NonNegative", "\"ReceivedBaseQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"WrittenOffBaseQty\" >= 0 AND \"ReturnedBaseQty\" >= 0 AND \"AcceptedCorrectionBaseQty\" >= 0");
            t.HasCheckConstraint("CK_StockTransferLineBatches_Counters_Max",
                "\"ReceivedBaseQty\" + \"DamagedBaseQty\" + \"WrittenOffBaseQty\" + \"ReturnedBaseQty\" + \"AcceptedCorrectionBaseQty\" <= \"DispatchedBaseQty\"");
        });
    }
}

public class StockTransferReceiptConfiguration : IEntityTypeConfiguration<StockTransferReceipt>
{
    public void Configure(EntityTypeBuilder<StockTransferReceipt> builder)
    {
        builder.HasKey(r => r.Id);
        builder.HasAlternateKey(r => new { r.CompanyId, r.Id });
        // Lets receipt lines tie their Kind to the header (composite FK).
        builder.HasAlternateKey(r => new { r.CompanyId, r.Id, r.Kind }).HasName("AK_StockTransferReceipts_CompanyId_Id_Kind");
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Notes).HasMaxLength(300);

        builder.HasIndex(r => new { r.CompanyId, r.TransferId, r.Sequence }, "IX_StockTransferReceipts_CompanyId_TransferId_Sequence").IsUnique();

        builder.HasOne<StockTransfer>().WithMany(t => t.Receipts).HasForeignKey(r => new { r.CompanyId, r.TransferId })
            .HasPrincipalKey(t => new { t.CompanyId, t.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferReceipts_StockTransfers");
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.ByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransferReceipts_Users_By");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StockTransferReceipts_Sequence_Positive", "\"Sequence\" > 0");
            t.HasCheckConstraint("CK_StockTransferReceipts_Kind_Valid", "\"Kind\" IN ('Receipt', 'Resolution')");
        });
    }
}

public class StockTransferReceiptLineConfiguration : IEntityTypeConfiguration<StockTransferReceiptLine>
{
    public void Configure(EntityTypeBuilder<StockTransferReceiptLine> builder)
    {
        builder.HasKey(l => new { l.ReceiptId, l.TransferLineId });
        builder.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.UnitCode).HasMaxLength(10).IsRequired();
        builder.Property(l => l.ConversionFactorToBase).HasColumnType("numeric(28,10)");
        foreach (var p in new[] { nameof(StockTransferReceiptLine.AcceptedQty), nameof(StockTransferReceiptLine.AcceptedBaseQty), nameof(StockTransferReceiptLine.DamagedQty),
                     nameof(StockTransferReceiptLine.DamagedBaseQty), nameof(StockTransferReceiptLine.WrittenOffBaseQty), nameof(StockTransferReceiptLine.ReturnedBaseQty) })
        {
            builder.Property(p).HasColumnType("numeric(18,3)");
        }

        builder.HasIndex(l => new { l.CompanyId, l.TransferLineId }, "IX_StockTransferReceiptLines_CompanyId_TransferLineId");
        builder.HasIndex(l => new { l.ProductId, l.ProductUnitId }, "IX_StockTransferReceiptLines_ProductId_ProductUnitId");

        // (CompanyId, ReceiptId, Kind) -> Receipts AK: same company AND the line's Kind equals the header's Kind.
        builder.HasOne<StockTransferReceipt>().WithMany(r => r.Lines).HasForeignKey(l => new { l.CompanyId, l.ReceiptId, l.Kind })
            .HasPrincipalKey(r => new { r.CompanyId, r.Id, r.Kind }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferReceiptLines_Receipts");
        builder.HasOne<StockTransferLine>().WithMany().HasForeignKey(l => new { l.CompanyId, l.TransferLineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferReceiptLines_Lines");
        builder.HasOne<Product>().WithMany().HasForeignKey(l => new { l.CompanyId, l.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferReceiptLines_Products");
        builder.HasOne<ProductUnit>().WithMany().HasForeignKey(l => new { l.ProductId, l.ProductUnitId })
            .HasPrincipalKey(u => new { u.ProductId, u.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockTransferReceiptLines_ProductUnits");
        builder.HasOne<Unit>().WithMany().HasForeignKey(l => l.UnitId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockTransferReceiptLines_Units");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StockTransferReceiptLines_Factor_Positive", "\"ConversionFactorToBase\" > 0");
            t.HasCheckConstraint("CK_StockTransferReceiptLines_Quantities_NonNegative",
                "\"AcceptedQty\" >= 0 AND \"AcceptedBaseQty\" >= 0 AND \"DamagedQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"WrittenOffBaseQty\" >= 0 AND \"ReturnedBaseQty\" >= 0");
            t.HasCheckConstraint("CK_StockTransferReceiptLines_Arithmetic",
                "\"AcceptedQty\" * \"ConversionFactorToBase\" = \"AcceptedBaseQty\" AND \"DamagedQty\" * \"ConversionFactorToBase\" = \"DamagedBaseQty\"");
            // Kind consistency: Receipt = accepted/damaged only; Resolution = written-off/returned only. The header Kind is tied by the composite FK.
            t.HasCheckConstraint("CK_StockTransferReceiptLines_Kind_Quantities",
                "(\"Kind\" = 'Receipt' AND \"WrittenOffBaseQty\" = 0 AND \"ReturnedBaseQty\" = 0 AND \"AcceptedBaseQty\" + \"DamagedBaseQty\" > 0)"
                + " OR (\"Kind\" = 'Resolution' AND \"AcceptedBaseQty\" = 0 AND \"DamagedBaseQty\" = 0 AND \"WrittenOffBaseQty\" + \"ReturnedBaseQty\" > 0)");
        });
    }
}
