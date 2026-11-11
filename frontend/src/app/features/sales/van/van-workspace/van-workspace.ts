import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import { problemMessage, toProblem } from '../../../../core/http/api-errors';
import type { StockLocation } from '../../../../shared/locations/locations.models';
import { StockLocationsService } from '../../../../shared/locations/stock-locations.service';
import { UiButton } from '../../../../shared/ui/button/button';
import { formatQuantity } from '../../../../shared/units/unit-conversion';
import { StockService } from '../../../inventory/stock/stock.service';
import type { StockOverviewItem } from '../../../inventory/stock/stock.models';
import { VanTransferWizard, type VanTransferMode } from '../van-transfer-wizard/van-transfer-wizard';

const STOCK_PAGE = 50;

/** D11: pick a vehicle, see what it carries, sell from it (POS), and load / unload it through a transfer. */
@Component({
  selector: 'app-van-workspace',
  standalone: true,
  imports: [RouterLink, UiButton, VanTransferWizard],
  templateUrl: './van-workspace.html',
  styleUrl: './van-workspace.scss',
})
export class VanWorkspace implements OnInit {
  private readonly locations = inject(StockLocationsService);
  private readonly stock = inject(StockService);
  private readonly auth = inject(AuthService);
  private readonly context = inject(ContextService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly vehicles = signal<StockLocation[]>([]);
  readonly vehicleId = signal<string | null>(null);
  readonly loadingVehicles = signal(true);
  readonly vehicleError = signal<string | null>(null);
  readonly stockItems = signal<StockOverviewItem[]>([]);
  readonly stockTotal = signal(0);
  readonly stockError = signal<string | null>(null);
  readonly stockLoading = signal(false);
  readonly wizard = signal<VanTransferMode | null>(null);

  readonly canListLocations = computed(() => this.auth.hasPermission('StockLocations.View'));
  readonly canViewStock = computed(() => this.auth.hasPermission('Inventory.View'));
  readonly canTransfer = computed(() => this.auth.hasPermission('StockTransfers.Create'));
  readonly vehicle = computed(() => this.vehicles().find((v) => v.id === this.vehicleId()) ?? null);
  readonly qty = (n: number) => formatQuantity(n, 0);

  private readonly reloadStock = effect(() => {
    const id = this.vehicleId();
    if (id && untracked(() => this.canViewStock())) untracked(() => this.loadStock(id));
  });

  ngOnInit(): void {
    if (!this.canListLocations()) {
      this.loadingVehicles.set(false);
      return;
    }
    const branchId = this.context.workingBranch()?.id;
    this.locations.list({ type: 'Vehicle', ...(branchId ? { branchId } : {}) }).subscribe({
      next: (all) => {
        const vehicles = all.filter((l) => l.isActive && l.locationType === 'Vehicle');
        this.vehicles.set(vehicles);
        this.loadingVehicles.set(false);
        const wanted = this.route.snapshot.queryParamMap.get('vehicleId');
        const pick = vehicles.find((v) => v.id === wanted) ?? (vehicles.length === 1 ? vehicles[0] : null);
        this.vehicleId.set(pick?.id ?? null);
      },
      error: (err) => {
        this.loadingVehicles.set(false);
        this.vehicleError.set(problemMessage(toProblem(err)));
      },
    });
  }

  selectVehicle(id: string): void {
    this.wizard.set(null);
    this.vehicleId.set(id || null);
  }

  /** POS with the vehicle preselected (`/sales/new?locationId=`; see the report note: POS must read this parameter). */
  sell(): void {
    const id = this.vehicleId();
    if (id) this.router.navigate(['/sales/new'], { queryParams: { locationId: id } });
  }

  openWizard(mode: VanTransferMode): void {
    this.wizard.set(mode);
  }

  onChanged(): void {
    const id = this.vehicleId();
    if (id && this.canViewStock()) this.loadStock(id);
  }

  private loadStock(id: string): void {
    this.stockLoading.set(true);
    this.stockError.set(null);
    this.stock.overview({ stockLocationId: id, page: 1, pageSize: STOCK_PAGE }).subscribe({
      next: (res) => {
        if (this.vehicleId() !== id) return;
        this.stockItems.set(res.items);
        this.stockTotal.set(res.total);
        this.stockLoading.set(false);
      },
      error: (err) => {
        this.stockLoading.set(false);
        this.stockError.set(problemMessage(toProblem(err)));
      },
    });
  }
}
