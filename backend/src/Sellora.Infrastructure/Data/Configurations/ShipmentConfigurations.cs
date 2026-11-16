using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

// Phase 04 (FI-D29..D31): shipments, vehicles, consignments, allocations and stock reservations. Same conventions as the transfer tables:
// same-company composite FKs, navigation-less towards masters, Restrict, string enums, explicit names under 63 bytes.

public class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> builder)
    {
        builder.HasKey(s => s.Id);
        builder.HasAlternateKey(s => new { s.CompanyId, s.Id });
        builder.Property(s => s.ShipmentNo).HasMaxLength(30).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.TransportMode).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.TransportVia).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.ProviderName).HasMaxLength(120);
        builder.Property(s => s.ProviderContact).HasMaxLength(120);
        builder.Property(s => s.TransportReference).HasMaxLength(100);
        builder.Property(s => s.Notes).HasMaxLength(500);
        builder.Property(s => s.CancellationReason).HasMaxLength(300);

        builder.HasIndex(s => new { s.CompanyId, s.ShipmentNo }, "IX_Shipments_CompanyId_ShipmentNo").IsUnique();
        builder.HasIndex(s => new { s.CompanyId, s.Status }, "IX_Shipments_CompanyId_Status");
        builder.HasIndex(s => new { s.CompanyId, s.BranchId, s.CreatedAt }, "IX_Shipments_CompanyId_BranchId_CreatedAt");
        builder.HasIndex(s => new { s.CompanyId, s.SourceLocationId }, "IX_Shipments_CompanyId_SourceLocationId");

        // MATCH SIMPLE: a NULL BranchId (company-level source) skips the check.
        builder.HasOne<Branch>().WithMany().HasForeignKey(s => new { s.CompanyId, s.BranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Shipments_Branches_BranchId");
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(s => new { s.CompanyId, s.SourceLocationId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Shipments_StockLocations_Source");
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_Shipments_Users_CreatedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_Shipments_Users_UpdatedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.CancelledByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_Shipments_Users_CancelledBy");
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.DispatchedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_Shipments_Users_DispatchedBy");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Shipments_Status_Valid", "\"Status\" IN ('Draft', 'Loading', 'ReadyToDispatch', 'Cancelled', 'Dispatched', 'PartiallyDelivered', 'Delivered')");
            t.HasCheckConstraint("CK_Shipments_Status_Dispatched",
                "(\"Status\" IN ('Dispatched', 'PartiallyDelivered', 'Delivered')) = (\"DispatchedAt\" IS NOT NULL) AND (\"DispatchedAt\" IS NULL) = (\"DispatchedByUserId\" IS NULL)");
            t.HasCheckConstraint("CK_Shipments_TransportMode_Valid", "\"TransportMode\" IN ('Road', 'Air', 'Sea', 'Rail', 'Courier', 'HandCarry')");
            t.HasCheckConstraint("CK_Shipments_TransportVia_Valid", "\"TransportVia\" IN ('OwnFleet', 'ExternalProvider')");
            t.HasCheckConstraint("CK_Shipments_Provider_Required", "\"TransportVia\" <> 'ExternalProvider' OR COALESCE(char_length(\"ProviderName\"), 0) > 0");
            t.HasCheckConstraint("CK_Shipments_Status_Cancellation",
                "(\"Status\" = 'Cancelled') = (\"CancelledAt\" IS NOT NULL) AND (\"CancelledAt\" IS NULL) = (\"CancelledByUserId\" IS NULL) AND (\"CancellationReason\" IS NULL OR \"Status\" = 'Cancelled')");
        });
    }
}

public class ShipmentVehicleConfiguration : IEntityTypeConfiguration<ShipmentVehicle>
{
    public void Configure(EntityTypeBuilder<ShipmentVehicle> builder)
    {
        builder.HasKey(v => v.Id);
        builder.HasAlternateKey(v => new { v.CompanyId, v.Id });
        // Target of Consignments' (ShipmentId, ShipmentVehicleId) composite FK: a consignment can only use a leg of its own shipment.
        builder.HasAlternateKey(v => new { v.ShipmentId, v.Id }).HasName("AK_ShipmentVehicles_ShipmentId_Id");
        builder.Property(v => v.ExternalVehicleNumber).HasMaxLength(40);
        builder.Property(v => v.ExternalDescription).HasMaxLength(120);
        builder.Property(v => v.DriverName).HasMaxLength(120);
        builder.Property(v => v.DriverPhone).HasMaxLength(30);

        builder.HasIndex(v => new { v.ShipmentId, v.Sequence }, "IX_ShipmentVehicles_ShipmentId_Sequence").IsUnique();
        builder.HasIndex(v => new { v.ShipmentId, v.VehicleId }, "IX_ShipmentVehicles_ShipmentId_VehicleId").IsUnique().HasFilter("\"VehicleId\" IS NOT NULL");
        builder.HasIndex(v => new { v.CompanyId, v.VehicleId }, "IX_ShipmentVehicles_CompanyId_VehicleId").HasFilter("\"VehicleId\" IS NOT NULL");

        builder.HasOne<Shipment>().WithMany(s => s.Vehicles).HasForeignKey(v => new { v.CompanyId, v.ShipmentId })
            .HasPrincipalKey(s => new { s.CompanyId, s.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ShipmentVehicles_Shipments");
        // MATCH SIMPLE: external vehicles (NULL VehicleId) skip the check.
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(v => new { v.CompanyId, v.VehicleId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ShipmentVehicles_Vehicles");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ShipmentVehicles_Sequence_Positive", "\"Sequence\" > 0");
            t.HasCheckConstraint("CK_ShipmentVehicles_Vehicle_Xor_External", "(\"VehicleId\" IS NULL) <> (\"ExternalVehicleNumber\" IS NULL)");
        });
    }
}

public class ConsignmentConfiguration : IEntityTypeConfiguration<Consignment>
{
    public void Configure(EntityTypeBuilder<Consignment> builder)
    {
        builder.HasKey(c => c.Id);
        builder.HasAlternateKey(c => new { c.CompanyId, c.Id });
        builder.Property(c => c.ConsignmentNo).HasMaxLength(30).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.CancellationReason).HasMaxLength(300);

        builder.HasIndex(c => new { c.CompanyId, c.ConsignmentNo }, "IX_Consignments_CompanyId_ConsignmentNo").IsUnique();
        // One active consignment per destination per shipment.
        builder.HasIndex(c => new { c.ShipmentId, c.DestinationLocationId }, "IX_Consignments_ShipmentId_Destination_Active").IsUnique().HasFilter("\"Status\" <> 'Cancelled'");
        builder.HasIndex(c => new { c.CompanyId, c.DestinationLocationId }, "IX_Consignments_CompanyId_DestinationLocationId");
        builder.HasIndex(c => new { c.CompanyId, c.DestinationBranchId }, "IX_Consignments_CompanyId_DestinationBranchId").HasFilter("\"DestinationBranchId\" IS NOT NULL");
        builder.HasIndex(c => new { c.ShipmentId, c.ShipmentVehicleId }, "IX_Consignments_ShipmentId_ShipmentVehicleId").HasFilter("\"ShipmentVehicleId\" IS NOT NULL");

        builder.HasOne<Shipment>().WithMany(s => s.Consignments).HasForeignKey(c => new { c.CompanyId, c.ShipmentId })
            .HasPrincipalKey(s => new { s.CompanyId, s.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Consignments_Shipments");
        // Same shipment: (ShipmentId, ShipmentVehicleId) -> ShipmentVehicles (ShipmentId, Id); MATCH SIMPLE skips an unassigned leg.
        builder.HasOne<ShipmentVehicle>().WithMany().HasForeignKey(c => new { c.ShipmentId, c.ShipmentVehicleId })
            .HasPrincipalKey(v => new { v.ShipmentId, v.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Consignments_ShipmentVehicles_SameShipment");
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(c => new { c.CompanyId, c.DestinationLocationId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Consignments_StockLocations_Destination");
        builder.HasOne<Branch>().WithMany().HasForeignKey(c => new { c.CompanyId, c.DestinationBranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Consignments_Branches_DestinationBranchId");
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_Consignments_Users_CreatedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_Consignments_Users_UpdatedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.LoadedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_Consignments_Users_LoadedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.CancelledByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_Consignments_Users_CancelledBy");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Consignments_Status_Valid", "\"Status\" IN ('Planned', 'Loaded', 'Cancelled', 'InTransit', 'PartiallyReceived', 'Received')");
            t.HasCheckConstraint("CK_Consignments_Status_Dispatched", "(\"Status\" IN ('InTransit', 'PartiallyReceived', 'Received')) = (\"DispatchedAt\" IS NOT NULL)");
            t.HasCheckConstraint("CK_Consignments_Status_Cancellation",
                "(\"Status\" = 'Cancelled') = (\"CancelledAt\" IS NOT NULL) AND (\"CancelledAt\" IS NULL) = (\"CancelledByUserId\" IS NULL) AND (\"CancellationReason\" IS NULL OR \"Status\" = 'Cancelled')");
            // Loaded needs its stamp; a Planned consignment has none.
            t.HasCheckConstraint("CK_Consignments_Status_Loaded", "(\"Status\" <> 'Loaded' OR \"LoadedAt\" IS NOT NULL) AND (\"Status\" <> 'Planned' OR \"LoadedAt\" IS NULL) AND (\"LoadedAt\" IS NULL) = (\"LoadedByUserId\" IS NULL)");
        });
    }
}

public class ConsignmentLineConfiguration : IEntityTypeConfiguration<ConsignmentLine>
{
    public void Configure(EntityTypeBuilder<ConsignmentLine> builder)
    {
        builder.HasKey(l => l.Id);
        builder.HasAlternateKey(l => new { l.CompanyId, l.Id });
        builder.Property(l => l.UnitCode).HasMaxLength(10).IsRequired();
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.ConversionFactorToBase).HasColumnType("numeric(28,10)");
        builder.Property(l => l.EnteredQty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.AllocatedBaseQty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.LoadedBaseQty).HasColumnType("numeric(18,3)").HasDefaultValue(0m);
        foreach (var p in new[] { nameof(ConsignmentLine.DispatchedBaseQty), nameof(ConsignmentLine.AcceptedBaseQty), nameof(ConsignmentLine.DamagedBaseQty),
                     nameof(ConsignmentLine.RejectedBaseQty), nameof(ConsignmentLine.MissingBaseQty), nameof(ConsignmentLine.PostedBaseQty) })
        {
            builder.Property(p).HasColumnType("numeric(18,3)").HasDefaultValue(0m);
        }

        builder.HasIndex(l => new { l.ConsignmentId, l.StockTransferLineId }, "IX_ConsignmentLines_ConsignmentId_TransferLine_Active").IsUnique().HasFilter("\"Status\" = 'Active'");
        builder.HasIndex(l => new { l.CompanyId, l.StockTransferLineId }, "IX_ConsignmentLines_CompanyId_StockTransferLineId");
        builder.HasIndex(l => new { l.CompanyId, l.StockTransferId }, "IX_ConsignmentLines_CompanyId_StockTransferId");
        builder.HasIndex(l => new { l.ProductId, l.ProductUnitId }, "IX_ConsignmentLines_ProductId_ProductUnitId");
        builder.HasIndex(l => l.UnitId, "IX_ConsignmentLines_UnitId");

        builder.HasOne<Consignment>().WithMany(c => c.Lines).HasForeignKey(l => new { l.CompanyId, l.ConsignmentId })
            .HasPrincipalKey(c => new { c.CompanyId, c.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ConsignmentLines_Consignments");
        builder.HasOne<StockTransferLine>().WithMany().HasForeignKey(l => new { l.CompanyId, l.StockTransferLineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ConsignmentLines_StockTransferLines");
        builder.HasOne<StockTransfer>().WithMany().HasForeignKey(l => new { l.CompanyId, l.StockTransferId })
            .HasPrincipalKey(t => new { t.CompanyId, t.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ConsignmentLines_StockTransfers");
        builder.HasOne<Product>().WithMany().HasForeignKey(l => new { l.CompanyId, l.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ConsignmentLines_Products");
        builder.HasOne<ProductUnit>().WithMany().HasForeignKey(l => new { l.ProductId, l.ProductUnitId })
            .HasPrincipalKey(u => new { u.ProductId, u.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ConsignmentLines_ProductUnits");
        builder.HasOne<Unit>().WithMany().HasForeignKey(l => l.UnitId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_ConsignmentLines_Units");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ConsignmentLines_Status_Valid", "\"Status\" IN ('Active', 'Cancelled')");
            t.HasCheckConstraint("CK_ConsignmentLines_Factor_Positive", "\"ConversionFactorToBase\" > 0");
            t.HasCheckConstraint("CK_ConsignmentLines_Allocated_Arithmetic", "\"EnteredQty\" >= 0 AND \"EnteredQty\" * \"ConversionFactorToBase\" = \"AllocatedBaseQty\"");
            t.HasCheckConstraint("CK_ConsignmentLines_Allocated_Positive", "\"Status\" <> 'Active' OR \"AllocatedBaseQty\" > 0");
            t.HasCheckConstraint("CK_ConsignmentLines_Loaded_Valid", "\"LoadedBaseQty\" >= 0 AND \"LoadedBaseQty\" <= \"AllocatedBaseQty\"");
            t.HasCheckConstraint("CK_ConsignmentLines_Status_Cancellation", "(\"Status\" = 'Cancelled') = (\"CancelledAt\" IS NOT NULL)");
            // FI-D34/D35: after dispatch the unloaded remainder is released, so Allocated shrinks to Dispatched; Posted is the accepted share already moved to the destination.
            t.HasCheckConstraint("CK_ConsignmentLines_Dispatched_Valid", "\"DispatchedBaseQty\" >= 0 AND \"DispatchedBaseQty\" <= \"AllocatedBaseQty\"");
            t.HasCheckConstraint("CK_ConsignmentLines_Receiving_NonNegative", "\"AcceptedBaseQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"RejectedBaseQty\" >= 0 AND \"MissingBaseQty\" >= 0 AND \"PostedBaseQty\" >= 0");
            t.HasCheckConstraint("CK_ConsignmentLines_Receiving_Max", "\"AcceptedBaseQty\" + \"DamagedBaseQty\" + \"RejectedBaseQty\" + \"MissingBaseQty\" <= \"DispatchedBaseQty\"");
            t.HasCheckConstraint("CK_ConsignmentLines_Posted_Valid", "\"PostedBaseQty\" <= \"AcceptedBaseQty\"");
        });
    }
}

public class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.BaseQty).HasColumnType("numeric(18,3)");
        builder.Property(r => r.ReleaseReason).HasMaxLength(300);

        // FI-D5: one live reservation per consignment line (allocate extends it instead of adding another).
        builder.HasIndex(r => r.ConsignmentLineId, "IX_StockReservations_ConsignmentLineId_Active").IsUnique().HasFilter("\"Status\" = 'Active'");
        builder.HasIndex(r => new { r.CompanyId, r.SourceLocationId, r.ProductId }, "IX_StockReservations_Location_Product_Active").HasFilter("\"Status\" = 'Active'");
        // Oldest-first report of live reservations.
        builder.HasIndex(r => new { r.CompanyId, r.CreatedAt }, "IX_StockReservations_CompanyId_CreatedAt_Active").HasFilter("\"Status\" = 'Active'");
        builder.HasIndex(r => new { r.CompanyId, r.ConsignmentLineId }, "IX_StockReservations_CompanyId_ConsignmentLineId");

        builder.HasOne<ConsignmentLine>().WithMany().HasForeignKey(r => new { r.CompanyId, r.ConsignmentLineId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockReservations_ConsignmentLines");
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(r => new { r.CompanyId, r.SourceLocationId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockReservations_StockLocations_Source");
        builder.HasOne<Product>().WithMany().HasForeignKey(r => new { r.CompanyId, r.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockReservations_Products");
        // The stock row the reservation is counted in (StockItems PK is (StockLocationId, ProductId)).
        builder.HasOne<StockItem>().WithMany().HasForeignKey(r => new { r.SourceLocationId, r.ProductId })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockReservations_StockItems");
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockReservations_Users_CreatedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.ReleasedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_StockReservations_Users_ReleasedBy");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StockReservations_Status_Valid", "\"Status\" IN ('Active', 'Released', 'Consumed')");
            t.HasCheckConstraint("CK_StockReservations_BaseQty_Positive", "\"BaseQty\" > 0");
            t.HasCheckConstraint("CK_StockReservations_Status_Released", "(\"Status\" = 'Released') = (\"ReleasedAt\" IS NOT NULL) AND (\"ReleasedAt\" IS NULL) = (\"ReleasedByUserId\" IS NULL) AND (\"ReleaseReason\" IS NULL OR \"Status\" = 'Released')");
            t.HasCheckConstraint("CK_StockReservations_Status_Consumed", "(\"Status\" = 'Consumed') = (\"ConsumedAt\" IS NOT NULL)");
        });
    }
}

public class ConsignmentReceiptConfiguration : IEntityTypeConfiguration<ConsignmentReceipt>
{
    public void Configure(EntityTypeBuilder<ConsignmentReceipt> builder)
    {
        builder.HasKey(r => r.Id);
        builder.HasAlternateKey(r => new { r.CompanyId, r.Id });
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Notes).HasMaxLength(500);

        builder.HasIndex(r => new { r.ConsignmentId, r.Sequence }, "IX_ConsignmentReceipts_ConsignmentId_Sequence").IsUnique();
        builder.HasIndex(r => new { r.CompanyId, r.ConsignmentId }, "IX_ConsignmentReceipts_CompanyId_ConsignmentId");
        builder.HasIndex(r => new { r.CompanyId, r.Status }, "IX_ConsignmentReceipts_CompanyId_Status_Verified").HasFilter("\"Status\" = 'Verified'");

        builder.HasOne<Consignment>().WithMany().HasForeignKey(r => new { r.CompanyId, r.ConsignmentId })
            .HasPrincipalKey(c => new { c.CompanyId, c.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ConsignmentReceipts_Consignments");
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.VerifiedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_ConsignmentReceipts_Users_VerifiedBy");
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.PostedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_ConsignmentReceipts_Users_PostedBy");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ConsignmentReceipts_Status_Valid", "\"Status\" IN ('Verified', 'Posted')");
            t.HasCheckConstraint("CK_ConsignmentReceipts_Sequence_Positive", "\"Sequence\" > 0");
            t.HasCheckConstraint("CK_ConsignmentReceipts_Status_Posted", "(\"Status\" = 'Posted') = (\"PostedAt\" IS NOT NULL) AND (\"PostedAt\" IS NULL) = (\"PostedByUserId\" IS NULL)");
        });
    }
}

public class ConsignmentReceiptLineConfiguration : IEntityTypeConfiguration<ConsignmentReceiptLine>
{
    public void Configure(EntityTypeBuilder<ConsignmentReceiptLine> builder)
    {
        builder.HasKey(l => l.Id);
        builder.HasAlternateKey(l => new { l.CompanyId, l.Id });
        builder.Property(l => l.UnitCode).HasMaxLength(10).IsRequired();
        builder.Property(l => l.ConversionFactorToBase).HasColumnType("numeric(28,10)");
        foreach (var p in new[] { nameof(ConsignmentReceiptLine.AcceptedEnteredQty), nameof(ConsignmentReceiptLine.AcceptedBaseQty), nameof(ConsignmentReceiptLine.DamagedEnteredQty),
                     nameof(ConsignmentReceiptLine.DamagedBaseQty), nameof(ConsignmentReceiptLine.RejectedEnteredQty), nameof(ConsignmentReceiptLine.RejectedBaseQty),
                     nameof(ConsignmentReceiptLine.MissingEnteredQty), nameof(ConsignmentReceiptLine.MissingBaseQty) })
        {
            builder.Property(p).HasColumnType("numeric(18,3)");
        }

        builder.HasIndex(l => new { l.ReceiptId, l.ConsignmentLineId }, "IX_ConsignmentReceiptLines_ReceiptId_ConsignmentLineId").IsUnique();
        builder.HasIndex(l => new { l.CompanyId, l.ConsignmentLineId }, "IX_ConsignmentReceiptLines_CompanyId_ConsignmentLineId");
        builder.HasIndex(l => new { l.ProductId, l.ProductUnitId }, "IX_ConsignmentReceiptLines_ProductId_ProductUnitId");
        builder.HasIndex(l => l.UnitId, "IX_ConsignmentReceiptLines_UnitId");

        builder.HasOne<ConsignmentReceipt>().WithMany(r => r.Lines).HasForeignKey(l => new { l.CompanyId, l.ReceiptId })
            .HasPrincipalKey(r => new { r.CompanyId, r.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ConsignmentReceiptLines_Receipts");
        builder.HasOne<ConsignmentLine>().WithMany().HasForeignKey(l => new { l.CompanyId, l.ConsignmentLineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ConsignmentReceiptLines_ConsignmentLines");
        builder.HasOne<Product>().WithMany().HasForeignKey(l => new { l.CompanyId, l.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ConsignmentReceiptLines_Products");
        builder.HasOne<ProductUnit>().WithMany().HasForeignKey(l => new { l.ProductId, l.ProductUnitId })
            .HasPrincipalKey(u => new { u.ProductId, u.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ConsignmentReceiptLines_ProductUnits");
        builder.HasOne<Unit>().WithMany().HasForeignKey(l => l.UnitId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_ConsignmentReceiptLines_Units");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ConsignmentReceiptLines_Factor_Positive", "\"ConversionFactorToBase\" > 0");
            t.HasCheckConstraint("CK_ConsignmentReceiptLines_Qty_NonNegative",
                "\"AcceptedEnteredQty\" >= 0 AND \"AcceptedBaseQty\" >= 0 AND \"DamagedEnteredQty\" >= 0 AND \"DamagedBaseQty\" >= 0 AND \"RejectedEnteredQty\" >= 0 AND \"RejectedBaseQty\" >= 0 AND \"MissingEnteredQty\" >= 0 AND \"MissingBaseQty\" >= 0");
            t.HasCheckConstraint("CK_ConsignmentReceiptLines_Arithmetic",
                "\"AcceptedEnteredQty\" * \"ConversionFactorToBase\" = \"AcceptedBaseQty\" AND \"DamagedEnteredQty\" * \"ConversionFactorToBase\" = \"DamagedBaseQty\" AND \"RejectedEnteredQty\" * \"ConversionFactorToBase\" = \"RejectedBaseQty\" AND \"MissingEnteredQty\" * \"ConversionFactorToBase\" = \"MissingBaseQty\"");
            t.HasCheckConstraint("CK_ConsignmentReceiptLines_AnyPositive", "\"AcceptedBaseQty\" + \"DamagedBaseQty\" + \"RejectedBaseQty\" + \"MissingBaseQty\" > 0");
        });
    }
}
