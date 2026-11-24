import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { UiCombobox, type ComboOption } from '../../../../shared/ui/combobox/combobox';
import { AuthService } from '../../../../core/auth/auth.service';
import {
  ClassificationEditorDialog,
  type ClassificationEditorTarget,
  type ClassificationSaved,
} from '../../../../shared/product-categories/classification-editor-dialog';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ProductCategoriesService } from '../../../../shared/product-categories/product-categories.service';
import type { ProductCategory, ProductSubcategory } from '../../../../shared/product-categories/product-categories.models';
import { ProductsService } from '../products.service';
import { UnitsService } from '../../../masters/units/units.service';
import { TaxesService } from '../../../masters/taxes/taxes.service';
import type { UnitItem } from '../../../masters/units/units.models';
import type { TaxItem } from '../../../masters/taxes/taxes.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';
import type { ProductDetail, UpsertProductRequest } from '../products.models';
import {
  ProductUnitsEditor,
  createBaseUnitRow,
  problemKeyToFormPath,
  sortUnitsForEditor,
  toProductUnitInputs,
  unitRowFromDto,
  type UnitRowGroup,
} from './product-units-editor/product-units-editor';

@Component({
  selector: 'app-product-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton, UiCombobox, ClassificationEditorDialog, ProductUnitsEditor],
  templateUrl: './product-form.html',
  styleUrl: './product-form.scss',
})
export class ProductForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly productsService = inject(ProductsService);
  private readonly unitsService = inject(UnitsService);
  private readonly categoriesService = inject(ProductCategoriesService);
  private readonly taxesService = inject(TaxesService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  readonly productId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.productId !== null && this.productId !== 'new');

  /** All units from the master (active and inactive). */
  readonly allUnits = signal<UnitItem[]>([]);
  /** Base unit of the loaded product (edit only); always kept selectable so edit never blanks. */
  private readonly currentBaseUnitId = signal<string | null>(null);

  /** Base-unit options: active, non-Packaging units, plus the product's current base unit. */
  readonly units = computed<UnitItem[]>(() => {
    const current = this.currentBaseUnitId();
    return this.allUnits().filter(
      (u) => u.id === current || (u.isActive && u.measurementCategory !== 'Packaging'),
    );
  });
  readonly taxes = signal<TaxItem[]>([]);
  /** All categories (active and inactive); inactive ones are offered only while they are the current selection. */
  readonly allCategories = signal<ProductCategory[]>([]);
  private readonly selectedCategoryId = signal('');
  private readonly selectedSubcategoryId = signal('');

  readonly categoryOptions = computed(() => this.allCategories().filter((c) => c.isActive || c.id === this.selectedCategoryId()));
  /**
   * With a category: that category's subcategories. Without one: every subcategory of the offered categories,
   * so picking one fills in the category. Inactive subcategories appear only while they are the current value.
   */
  readonly subcategoryOptions = computed<ProductSubcategory[]>(() => {
    const categoryId = this.selectedCategoryId();
    const subId = this.selectedSubcategoryId();
    const categories = categoryId
      ? this.allCategories().filter((c) => c.id === categoryId)
      : this.allCategories().filter((c) => c.isActive || c.subcategories.some((s) => s.id === subId));
    return categories.flatMap((c) => c.subcategories.filter((s) => s.isActive || s.id === subId));
  });
  readonly categoryComboOptions = computed<ComboOption[]>(() =>
    this.categoryOptions().map((c) => ({ value: c.id, label: c.name + (c.isActive ? '' : ' (inactive)') })),
  );
  /** Names such as "Others" repeat across categories, so the unfiltered list adds the category as a hint. */
  readonly subcategoryComboOptions = computed<ComboOption[]>(() => {
    const narrowed = !!this.selectedCategoryId();
    return this.subcategoryOptions().map((s) => ({
      value: s.id,
      label: s.name + (s.isActive ? '' : ' (inactive)'),
      hint: narrowed ? undefined : this.categoryName(s.categoryId),
    }));
  });
  readonly canManageCategories = computed(() => this.auth.hasPermission('Inventory.Categories'));
  /** Target of the "+ New subcategory" modal; null = closed. */
  readonly subcategoryEditor = signal<ClassificationEditorTarget | null>(null);
  /** Stock history or a locked unit row exists: the base unit cannot change (R9). */
  readonly isBaseUnitLocked = signal(false);

  readonly form = this.fb.nonNullable.group({
    sku: ['', Validators.required],
    name: ['', Validators.required],
    categoryId: [''],
    subcategoryId: [''],
    baseUnitId: ['', Validators.required],
    taxId: ['', Validators.required],
    reorderLevel: [0, [Validators.required, Validators.min(0)]],
    /** Row 0 is the base row, kept in sync with `baseUnitId`. */
    units: new FormArray<UnitRowGroup>([createBaseUnitRow('')]),
  });

  get unitRows(): FormArray<UnitRowGroup> {
    return this.form.controls.units;
  }

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  /** Base unit the base row and its direct children currently point at. */
  private syncedBaseUnitId = '';

  constructor() {
    this.form.controls.baseUnitId.valueChanges.pipe(takeUntilDestroyed()).subscribe((id) => this.syncBaseRow(id));
    this.form.controls.categoryId.valueChanges.pipe(takeUntilDestroyed()).subscribe((id) => this.onCategoryChanged(id));
    this.form.controls.subcategoryId.valueChanges.pipe(takeUntilDestroyed()).subscribe((id) => this.onSubcategoryChanged(id));
  }

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    this.unitsService.list().subscribe({ next: (units) => this.allUnits.set(units) });
    this.categoriesService.list(true).subscribe({ next: (list) => this.allCategories.set(list) });
    this.taxesService.list().subscribe({ next: (taxes) => this.taxes.set(taxes.filter((t) => t.isActive)) });

    if (this.isEdit()) {
      this.productsService.get(this.productId!).subscribe({
        next: (p) => this.loadProduct(p),
      });
    }
  }

  /** Populates the form, rebuilding the unit rows (base first) and applying the base-unit lock. */
  loadProduct(p: ProductDetail): void {
    this.currentBaseUnitId.set(p.baseUnitId);
    this.form.patchValue({
      sku: p.sku,
      name: p.name,
      categoryId: p.categoryId ?? '',
      subcategoryId: p.subcategoryId ?? '',
      baseUnitId: p.baseUnitId,
      taxId: p.taxId,
      reorderLevel: p.reorderLevel,
    });
    this.unitRows.clear();
    const units = sortUnitsForEditor(p.units);
    if (!units.some((x) => x.isBaseUnit)) this.unitRows.push(createBaseUnitRow(p.baseUnitId));
    for (const unit of units) this.unitRows.push(unitRowFromDto(unit));
    this.syncedBaseUnitId = p.baseUnitId;

    this.isBaseUnitLocked.set(p.isBaseUnitLocked);
    if (p.isBaseUnitLocked) this.form.controls.baseUnitId.disable({ emitEvent: false });
    else this.form.controls.baseUnitId.enable({ emitEvent: false });
    this.form.markAsPristine();
  }

  /** A category that the selected subcategory does not belong to clears it; clearing the category keeps it. */
  private onCategoryChanged(categoryId: string): void {
    this.selectedCategoryId.set(categoryId);
    const sub = this.findSubcategory(this.form.controls.subcategoryId.value);
    if (categoryId && sub && sub.categoryId !== categoryId) {
      this.form.controls.subcategoryId.setValue('', { emitEvent: false });
      this.selectedSubcategoryId.set('');
    }
  }

  /** Picking a subcategory sets its category, which narrows the subcategory list. */
  private onSubcategoryChanged(subcategoryId: string): void {
    this.selectedSubcategoryId.set(subcategoryId);
    const sub = this.findSubcategory(subcategoryId);
    if (sub && sub.categoryId !== this.form.controls.categoryId.value) {
      this.form.controls.categoryId.setValue(sub.categoryId, { emitEvent: false });
      this.form.controls.categoryId.markAsDirty();
      this.selectedCategoryId.set(sub.categoryId);
    }
  }

  private findSubcategory(id: string): ProductSubcategory | undefined {
    if (!id) return undefined;
    for (const c of this.allCategories()) {
      const s = c.subcategories.find((x) => x.id === id);
      if (s) return s;
    }
    return undefined;
  }

  private categoryName(id: string): string {
    return this.allCategories().find((c) => c.id === id)?.name ?? '';
  }

  openNewSubcategory(): void {
    this.subcategoryEditor.set({ kind: 'subcategory', categoryId: this.form.controls.categoryId.value || null, id: null });
  }

  /** Adds the created subcategory to its category's list and selects it (which also sets the category). */
  onSubcategoryCreated(result: ClassificationSaved): void {
    this.subcategoryEditor.set(null);
    if (result.kind !== 'subcategory') return;
    const sub = result.subcategory;
    this.allCategories.update((list) =>
      list.map((c) =>
        c.id === sub.categoryId ? { ...c, subcategories: [...c.subcategories.filter((s) => s.id !== sub.id), sub] } : c,
      ),
    );
    // Set the category explicitly too: the parent may be missing from a stale list, so the lookup could fail.
    this.form.controls.categoryId.setValue(sub.categoryId, { emitEvent: false });
    this.selectedCategoryId.set(sub.categoryId);
    this.form.controls.subcategoryId.setValue(sub.id);
    this.form.controls.categoryId.markAsDirty();
    this.form.controls.subcategoryId.markAsDirty();
  }

  /** Keeps the base row bound to the selected base unit and re-points rows that contained the old base. */
  private syncBaseRow(baseUnitId: string): void {
    const previous = this.syncedBaseUnitId;
    this.syncedBaseUnitId = baseUnitId;
    const rows = this.unitRows.controls;
    const base = rows.find((r) => r.controls.isBaseUnit.value);
    if (!base) return;
    base.controls.unitId.setValue(baseUnitId);
    if (!previous) return;
    for (const row of rows) {
      const inner = row.controls.innerUnitId;
      if (row !== base && !row.controls.isLocked.value && inner.value === previous) inner.setValue(baseUnitId);
    }
  }

  buildRequest(): UpsertProductRequest {
    const v = this.form.getRawValue();
    return {
      sku: v.sku,
      name: v.name,
      // A subcategory implies its category (the category can be cleared while a subcategory stays selected).
      categoryId: v.categoryId || this.findSubcategory(v.subcategoryId)?.categoryId || null,
      subcategoryId: v.subcategoryId || null,
      baseUnitId: v.baseUnitId,
      taxId: v.taxId,
      reorderLevel: v.reorderLevel,
      units: toProductUnitInputs(v.units),
    };
  }

  submit(): void {
    this.serverError.set(null);

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const request = this.buildRequest();

    const request$ = this.isEdit() ? this.productsService.update(this.productId!, request) : this.productsService.create(request);

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Product updated.' : 'Product created.');
        this.router.navigateByUrl('/inventory/products');
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.applyServerErrors(err.error as ApiProblem | undefined);
      },
    });
  }

  /**
   * Maps every ProblemDetails `errors` entry onto its control (`Units[2].InnerQuantity` → `units.2.innerQuantity`,
   * `Units[2]` → the row, `Units` → the array). Unmatched messages go to `serverError`.
   */
  applyServerErrors(problem: ApiProblem | undefined): void {
    const unmatched: string[] = [];
    for (const [field, messages] of Object.entries(problem?.errors ?? {})) {
      if (!messages?.length) continue;
      const control = this.form.get(problemKeyToFormPath(field));
      if (control) {
        control.setErrors({ ...control.errors, server: messages[0] });
        control.markAsTouched();
      } else {
        unmatched.push(messages[0]);
      }
    }
    if (unmatched.length > 0) this.serverError.set(unmatched.join(' '));
    else if (!problem?.errors || Object.keys(problem.errors).length === 0) {
      this.serverError.set(problem?.title ?? 'Something went wrong.');
    }
  }
}
