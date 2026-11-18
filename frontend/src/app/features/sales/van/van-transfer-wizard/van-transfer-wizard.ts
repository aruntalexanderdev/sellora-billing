import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormArray, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { applyProblem, toProblem } from '../../../../core/http/api-errors';
import { StockLocationsService } from '../../../../shared/locations/stock-locations.service';
import type { StockLocation } from '../../../../shared/locations/locations.models';
import { UiButton } from '../../../../shared/ui/button/button';
import { ContextService } from '../../../../core/context/context.service';
import { StockTransfersService } from '../../../inventory/stock-transfers/stock-transfers.service';
import { TransferLinesEditor, newTransferLine, type TransferLineGroup } from '../../../inventory/stock-transfers/transfer-lines-editor/transfer-lines-editor';
import { actionPermission } from '../../../inventory/stock-transfers/stock-transfer-rules';
import type { StockTransfer } from '../../../inventory/stock-transfers/stock-transfers.models';
import { VehiclesService } from '../../../company/vehicles/vehicles.service';
import { ConsignmentsService } from '../../../inventory/consignments/consignments.service';
import { ShipmentsService } from '../../../inventory/shipments/shipments.service';
import { ChainKeys, runVanChain, type ChainResult, type ChainStep } from '../van-transfer-chain';

export type VanTransferMode = 'load' | 'unload';

const STEP_LABEL: Record<ChainStep, string> = {
  create: 'Created and submitted',
  submit: 'Submitted',
  approve: 'Approved',
  shipment: 'Shipment created',
  allocate: 'Allocated to the shipment',
  load: 'Loaded',
  dispatch: 'Dispatched',
  receive: 'Receipt recorded',
  post: 'Receipt posted',
};

/**
 * Load vehicle (store/warehouse -> vehicle) / Unload vehicle (vehicle -> store). Creates the transfer as Requested and then
 * approves it, then ships it (own-fleet shipment on the van's vehicle: allocate, load, dispatch) and receives and posts it, for as long as the user holds the permission; otherwise it stops and links to the transfer.
 */
@Component({
  selector: 'app-van-transfer-wizard',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, TransferLinesEditor],
  templateUrl: './van-transfer-wizard.html',
  styleUrl: './van-transfer-wizard.scss',
})
export class VanTransferWizard implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(StockTransfersService);
  private readonly shipments = inject(ShipmentsService);
  private readonly consignments = inject(ConsignmentsService);
  private readonly vehicles = inject(VehiclesService);
  private readonly locationsService = inject(StockLocationsService);
  private readonly auth = inject(AuthService);
  private readonly context = inject(ContextService);
  private keys = new ChainKeys();

  readonly mode = input.required<VanTransferMode>();
  readonly vehicle = input.required<StockLocation>();
  /** Emitted when at least one step posted (stock may have moved), so the host can refresh vehicle stock. */
  readonly changed = output<StockTransfer>();
  readonly closed = output<void>();

  readonly form = this.fb.nonNullable.group({
    counterpartId: ['', Validators.required],
    lines: this.fb.array<TransferLineGroup>([newTransferLine(this.fb)]) as FormArray<TransferLineGroup>,
  });

  readonly counterparts = signal<StockLocation[]>([]);
  readonly running = signal(false);
  readonly serverError = signal<string | null>(null);
  readonly result = signal<ChainResult | null>(null);
  readonly canViewTransfers = computed(() => this.auth.hasPermission('StockTransfers.View'));
  readonly title = computed(() => (this.mode() === 'load' ? `Load ${this.vehicle().name}` : `Unload ${this.vehicle().name}`));
  readonly counterpartLabel = computed(() => (this.mode() === 'load' ? 'Load from' : 'Unload to'));
  readonly stepLabel = (s: ChainStep) => STEP_LABEL[s];

  ngOnInit(): void {
    this.locationsService.list().subscribe({
      next: (all) => {
        const eligible = all.filter((l) => l.isActive && l.locationType !== 'Vehicle' && l.locationType !== 'Transit');
        this.counterparts.set(eligible);
        const branchId = this.context.workingBranch()?.id ?? null;
        const pick = eligible.find((l) => l.locationType === 'BranchStore' && l.branchId === branchId) ?? eligible[0];
        if (pick && !this.form.controls.counterpartId.value) this.form.controls.counterpartId.setValue(pick.id);
      },
    });
  }

  /** The permissions the chain would use, so the UI can tell the user up front what will happen. */
  readonly plan = computed(() => {
    const steps: { label: string; allowed: boolean; permission: string }[] = [
      { label: 'Create and submit', permission: actionPermission('submit'), allowed: false },
      { label: 'Approve', permission: actionPermission('approve'), allowed: false },
      { label: 'Shipment: plan, load, dispatch', permission: 'StockTransfers.Dispatch', allowed: false },
      { label: 'Receive and post', permission: actionPermission('receive'), allowed: false },
    ];
    return steps.map((s) => ({ ...s, allowed: this.auth.hasPermission(s.permission) }));
  });

  async run(): Promise<void> {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const vehicleId = this.vehicle().id;
    const load = this.mode() === 'load';
    const request = {
      sourceLocationId: load ? v.counterpartId : vehicleId,
      destinationLocationId: load ? vehicleId : v.counterpartId,
      notes: load ? 'Vehicle load' : 'Vehicle unload',
      lines: v.lines.map((l) => ({ productId: l.productId, productUnitId: l.productUnitId, quantity: l.quantity! })),
    };
    await this.execute(request, null);
  }

  /** Continues a chain that stopped on an error, with the same per-step idempotency keys. */
  async retry(): Promise<void> {
    const r = this.result();
    if (!r?.transfer) return;
    await this.execute(null, r.transfer);
  }

  private async execute(request: Parameters<typeof runVanChain>[1], existing: StockTransfer | null): Promise<void> {
    this.running.set(true);
    const res = await runVanChain(
      { transfers: this.service, shipments: this.shipments, consignments: this.consignments, vehicles: this.vehicles },
      request, existing, (c) => this.auth.hasPermission(c), this.keys, { vehicleLocationId: this.vehicle().id, currentUserId: this.auth.currentUser?.()?.id ?? null },
    );
    this.running.set(false);
    // Create refused with a validation problem: show the field errors on the form, nothing was created.
    if (res.reason === 'error' && !res.transfer) {
      this.serverError.set(applyProblem(this.form, toProblem(res.error)));
      this.result.set(null);
      return;
    }
    this.result.set(res);
    if (res.transfer && res.completed.length > 0) this.changed.emit(res.transfer);
    if (res.stoppedAt === null) this.keys = new ChainKeys();
  }

  close(): void {
    this.closed.emit();
  }
}
