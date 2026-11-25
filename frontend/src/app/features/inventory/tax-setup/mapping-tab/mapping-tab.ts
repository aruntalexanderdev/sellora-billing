import { HttpErrorResponse } from '@angular/common/http';
import { NgTemplateOutlet } from '@angular/common';
import { Component, inject, input, signal } from '@angular/core';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { CountryLookupDto } from '../../../../shared/geo/geo.models';
import { UiButton } from '../../../../shared/ui/button/button';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { problemFields, problemTitle } from '../problem-fields';
import type {
  TaxAssignment, TaxAssignmentTree, TaxEffective, TaxProfile, TaxScope, TaxTreeCategory, TaxTreeProduct, TaxTreeSubcategory,
} from '../tax-setup.models';
import { TaxSetupService } from '../tax-setup.service';
import { OVERRIDE_PAGE, effectiveText, sourceChip } from './tax-mapping.logic';

/** Context of the shared assignment row template. */
interface RowContext {
  name: string;
  meta: string;
  level: TaxScope;
  scopeId: string | null;
  assignment: TaxAssignment | null;
  effective: TaxEffective | null;
}

@Component({
  selector: 'app-tax-mapping-tab',
  standalone: true,
  imports: [NgTemplateOutlet, UiButton],
  styleUrl: '../tax-setup.shared.scss',
  styles: [`
    .node { border-top: 1px solid var(--color-border); }
    .children { margin-left: 18px; border-left: 2px solid var(--color-border-subtle); padding-left: 10px; min-width: 0; }
    .assign { display: flex; flex-wrap: wrap; gap: 8px; width: 100%; }
    .assign .field-input { flex: 1 1 170px; min-width: 0; padding: 6px 9px; }
    .assign label { font-size: 11px; color: var(--color-text-muted); display: block; }
    .caret { border: 0; background: transparent; cursor: pointer; font-size: 14px; padding: 0 6px 0 0; color: var(--color-text-muted); }
  `],
  template: `
    <ng-template #assignRow let-r>
      <div class="row" data-testid="map-row">
        <div class="main">
          <span class="strong">{{ r.name }}</span>
          @if (r.meta) { <span class="muted small"> {{ r.meta }}</span> }
          <div class="small muted">{{ effectiveText(r.assignment, r.effective) }}</div>
        </div>
        <span class="chip" [class.here]="chip(r).tone === 'here'" [class.override]="chip(r).tone === 'override'" data-testid="source-chip">{{ chip(r).text }}</span>
        @if (canManage()) {
          <div class="assign">
            <div>
              <label [for]="'s-' + r.level + (r.scopeId ?? '')">Sales profile</label>
              <select class="field-input" [id]="'s-' + r.level + (r.scopeId ?? '')" [value]="r.assignment?.salesProfile?.id ?? ''" (change)="onSales(r, $any($event.target).value)" [disabled]="busy()">
                @if (r.level !== 'Company') { <option value="">{{ r.level === 'Product' ? 'Remove override' : 'Inherit' }}</option> }
                @for (p of optionsFor(r.assignment?.salesProfile?.id); track p.id) { <option [value]="p.id">{{ p.name }}</option> }
              </select>
            </div>
            <div>
              <label [for]="'p-' + r.level + (r.scopeId ?? '')">Purchase profile</label>
              <select class="field-input" [id]="'p-' + r.level + (r.scopeId ?? '')" [value]="r.assignment?.purchaseProfile?.id ?? ''" (change)="onPurchase(r, $any($event.target).value)" [disabled]="busy() || !r.assignment">
                <option value="">Same as sales</option>
                @for (p of optionsFor(r.assignment?.purchaseProfile?.id); track p.id) { <option [value]="p.id">{{ p.name }}</option> }
              </select>
            </div>
            @if (r.level === 'Product' && r.assignment) {
              <button type="button" class="link danger" (click)="removeOverride(r.assignment)" [disabled]="busy()">Remove override</button>
            }
          </div>
        }
      </div>
    </ng-template>

    <ng-template #overrideList let-items let-key="key">
      @for (o of limited(items, key); track o.productId) {
        <ng-container *ngTemplateOutlet="assignRow; context: { $implicit: productRow(o) }" />
      }
      @if (count(items) > limited(items, key).length) {
        <button type="button" class="link" (click)="showAll(key)">Show all {{ count(items) }} overrides</button>
      }
    </ng-template>

    <div class="toolbar">
      <div>
        <label class="field-label" for="tm-country">Country</label>
        <select id="tm-country" class="field-input" [value]="countryId()" (change)="setCountry($any($event.target).value)">
          @for (c of countries(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
        </select>
      </div>
      <span class="grow"></span>
      @if (canManage()) {
        <ui-button variant="secondary" (onClick)="clearRedundant()" [disabled]="busy() || !countryId()">Clear overrides equal to the inherited profile</ui-button>
      }
    </div>

    <div class="panel">
      <p class="small muted">Each product uses the first profile found going up: product override, subcategory, category, company default.</p>
      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else if (loadFailed()) {
        <p class="field-error">Could not load the tax mapping. <button type="button" class="link" (click)="load()">Retry</button></p>
      } @else if (tree(); as t) {
        <div class="node" data-testid="company-default">
          <ng-container *ngTemplateOutlet="assignRow; context: { $implicit: companyRow(t) }" />
          @if (!t.companyDefault) { <div class="warn-box">No company default yet: products without a mapping have no tax profile. Choose one above.</div> }
        </div>

        @for (c of t.categories; track c.id) {
          <div class="node" data-testid="category-node">
            <div style="display:flex;align-items:flex-start">
              <button type="button" class="caret" (click)="toggle('c' + c.id)" [attr.aria-expanded]="isOpen('c' + c.id)" [attr.aria-label]="'Toggle ' + c.name">{{ isOpen('c' + c.id) ? '▾' : '▸' }}</button>
              <div style="flex:1;min-width:0"><ng-container *ngTemplateOutlet="assignRow; context: { $implicit: categoryRow(c) }" /></div>
            </div>
            @if (isOpen('c' + c.id)) {
              <div class="children">
                @for (s of c.subcategories; track s.id) {
                  <div class="node" data-testid="subcategory-node">
                    <ng-container *ngTemplateOutlet="assignRow; context: { $implicit: subRow(s) }" />
                    @if (s.overrideCount > 0) {
                      <button type="button" class="link" (click)="toggleOverrides('s' + s.id)" data-testid="show-overrides">{{ isOpen('o-s' + s.id) ? 'Hide' : 'Show' }} {{ s.overrideCount }} product override{{ s.overrideCount === 1 ? '' : 's' }}</button>
                      @if (isOpen('o-s' + s.id)) { <div class="children"><ng-container *ngTemplateOutlet="overrideList; context: { $implicit: s.overrides, key: 's' + s.id }" /></div> }
                    }
                  </div>
                } @empty {
                  <p class="small muted">No subcategories.</p>
                }
                @if (c.overrideCount - subOverrideCount(c) > 0 || c.overrides.length > 0) {
                  <button type="button" class="link" (click)="toggleOverrides('c' + c.id)" data-testid="show-overrides">{{ isOpen('o-c' + c.id) ? 'Hide' : 'Show' }} {{ c.overrideCount - subOverrideCount(c) }} override{{ c.overrideCount - subOverrideCount(c) === 1 ? '' : 's' }} without a subcategory</button>
                  @if (isOpen('o-c' + c.id)) { <div class="children"><ng-container *ngTemplateOutlet="overrideList; context: { $implicit: c.overrides, key: 'c' + c.id }" /></div> }
                }
              </div>
            }
          </div>
        }
        @if (t.uncategorisedOverrideCount > 0) {
          <div class="node">
            <button type="button" class="link" (click)="toggleOverrides('u')" data-testid="show-overrides">{{ isOpen('o-u') ? 'Hide' : 'Show' }} {{ t.uncategorisedOverrideCount }} override{{ t.uncategorisedOverrideCount === 1 ? '' : 's' }} of products without a category</button>
            @if (isOpen('o-u')) { <div class="children"><ng-container *ngTemplateOutlet="overrideList; context: { $implicit: t.uncategorisedOverrides, key: 'u' }" /></div> }
          </div>
        }
      }
    </div>
  `,
})
export class MappingTab {
  private readonly service = inject(TaxSetupService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);

  readonly countries = input<CountryLookupDto[]>([]);
  readonly canManage = input(false);

  readonly countryId = signal('');
  readonly tree = signal<TaxAssignmentTree | null>(null);
  readonly profiles = signal<TaxProfile[]>([]);
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly busy = signal(false);
  private readonly open = signal<ReadonlySet<string>>(new Set());
  /** Overrides are fetched only when a node asks for them (a migrated company has one per product). */
  private overridesLoaded = false;
  private readonly shown = signal<Record<string, number>>({});

  readonly effectiveText = effectiveText;

  constructor() {
    // The company's country comes back with the tree; the select follows it.
    this.load();
  }

  chip(r: RowContext) {
    return sourceChip(r.level, r.assignment, r.effective);
  }

  companyRow(t: TaxAssignmentTree): RowContext {
    return { name: 'Company default', meta: t.countryName, level: 'Company', scopeId: null, assignment: t.companyDefault, effective: null };
  }
  categoryRow(c: TaxTreeCategory): RowContext {
    return { name: c.name, meta: `${c.productCount} product${c.productCount === 1 ? '' : 's'}`, level: 'Category', scopeId: c.id, assignment: c.assignment, effective: c.effective };
  }
  subRow(s: TaxTreeSubcategory): RowContext {
    return { name: s.name, meta: `${s.productCount} product${s.productCount === 1 ? '' : 's'}${s.hsnCode ? ' · HSN ' + s.hsnCode : ''}`, level: 'Subcategory', scopeId: s.id, assignment: s.assignment, effective: s.effective };
  }
  productRow(p: TaxTreeProduct): RowContext {
    return { name: p.name, meta: p.sku, level: 'Product', scopeId: p.productId, assignment: p.assignment, effective: null };
  }
  subOverrideCount(c: TaxTreeCategory): number {
    return c.subcategories.reduce((n, s) => n + s.overrideCount, 0);
  }

  isOpen(key: string): boolean {
    return this.open().has(key);
  }
  toggle(key: string): void {
    this.open.update((s) => {
      const next = new Set(s);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  }
  toggleOverrides(key: string): void {
    const opening = !this.isOpen('o-' + key);
    this.toggle('o-' + key);
    if (opening && !this.overridesLoaded) {
      this.overridesLoaded = true;
      this.load(true);
    }
  }
  limited(items: unknown, key: string): TaxTreeProduct[] {
    return (items as TaxTreeProduct[]).slice(0, this.shown()[key] ?? OVERRIDE_PAGE);
  }
  count(items: unknown): number {
    return (items as TaxTreeProduct[]).length;
  }
  showAll(key: string): void {
    this.shown.update((s) => ({ ...s, [key]: Number.MAX_SAFE_INTEGER }));
  }

  /** Active profiles of the country, plus the row's current one even when inactive so the select never blanks. */
  optionsFor(currentId: string | undefined): TaxProfile[] {
    const all = this.profiles();
    return all.filter((p) => p.isActive || p.id === currentId);
  }

  setCountry(id: string): void {
    this.countryId.set(id);
    this.overridesLoaded = false;
    this.open.set(new Set());
    this.load();
  }

  load(quiet = false): void {
    if (!quiet) this.loading.set(true);
    this.loadFailed.set(false);
    this.service.tree(this.countryId() || null, this.overridesLoaded).subscribe({
      next: (t) => {
        this.tree.set(t);
        if (!this.countryId()) this.countryId.set(t.countryId);
        this.loading.set(false);
        this.service.listProfiles(t.countryId).subscribe({ next: (p) => this.profiles.set(p) });
      },
      error: () => {
        this.loadFailed.set(true);
        this.loading.set(false);
      },
    });
  }

  // ---- edits
  onSales(r: RowContext, profileId: string): void {
    const country = this.tree()?.countryId;
    if (!country) return;
    if (!profileId) {
      if (r.assignment) this.removeOverride(r.assignment);
      return;
    }
    this.save(r, { scopeType: r.level, scopeId: r.scopeId, countryId: country, salesProfileId: profileId, purchaseProfileId: r.assignment?.purchaseProfile?.id ?? null });
  }

  onPurchase(r: RowContext, profileId: string): void {
    const country = this.tree()?.countryId;
    if (!country || !r.assignment) return;
    this.save(r, { scopeType: r.level, scopeId: r.scopeId, countryId: country, salesProfileId: r.assignment.salesProfile.id, purchaseProfileId: profileId || null });
  }

  private save(r: RowContext, request: Parameters<TaxSetupService['upsertAssignment']>[0]): void {
    this.busy.set(true);
    this.service.upsertAssignment(request).subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.show(`${r.name}: tax profile saved.`);
        this.load(true);
      },
      error: (err: HttpErrorResponse) => this.fail(err),
    });
  }

  removeOverride(a: TaxAssignment): void {
    this.busy.set(true);
    this.service.deleteAssignment(a.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.show('Mapping removed; the profile is inherited again.');
        this.load(true);
      },
      error: (err: HttpErrorResponse) => this.fail(err),
    });
  }

  async clearRedundant(): Promise<void> {
    const t = this.tree();
    if (!t) return;
    const ok = await this.confirm.ask({
      title: 'Clear overrides equal to the inherited profile?',
      body: `Product overrides in ${t.countryName} that choose the same profile as their subcategory, category or company default are removed, so later changes to the category reach those products. Products keep the same tax.`,
      confirmLabel: 'Clear overrides',
    });
    if (!ok) return;
    this.busy.set(true);
    this.service.clearRedundantOverrides(t.countryId).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.toast.show(r.cleared === 0 ? 'No overrides were equal to the inherited profile.' : `${r.cleared} override${r.cleared === 1 ? '' : 's'} cleared.`);
        this.load(true);
      },
      error: (err: HttpErrorResponse) => this.fail(err),
    });
  }

  private fail(err: HttpErrorResponse): void {
    this.busy.set(false);
    const problem = err.error as ApiProblem | undefined;
    this.toast.show(Object.values(problemFields(problem))[0] ?? problemTitle(problem));
    this.load(true); // reset any select that moved
  }
}
