import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

type ModeGroup = FormGroup<{
  id: FormControl<string | null>;
  label: FormControl<string>;
  conversionFactorToBase: FormControl<number | null>;
}>;
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ProductsService } from '../products.service';
import { UnitsService } from '../../../masters/units/units.service';
import { TaxesService } from '../../../masters/taxes/taxes.service';
import type { UnitItem } from '../../../masters/units/units.models';
import type { TaxItem } from '../../../masters/taxes/taxes.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

@Component({
  selector: 'app-product-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './product-form.html',
  styleUrl: './product-form.scss',
})
export class ProductForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly productsService = inject(ProductsService);
  private readonly unitsService = inject(UnitsService);
  private readonly taxesService = inject(TaxesService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly productId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.productId !== null && this.productId !== 'new');

  /** All units from the master (active and inactive). */
  private readonly allUnits = signal<UnitItem[]>([]);
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

  readonly form = this.fb.nonNullable.group({
    sku: ['', Validators.required],
    name: ['', Validators.required],
    category: [''],
    baseUnitId: ['', Validators.required],
    taxId: ['', Validators.required],
    reorderLevel: [0, [Validators.required, Validators.min(0)]],
    sellingModes: this.fb.array<ModeGroup>([]),
  });

  get sellingModes(): FormArray<ModeGroup> {
    return this.form.controls.sellingModes;
  }

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  private saved = false;

  hasUnsavedChanges(): boolean {
    return !this.saved && this.form.dirty;
  }

  ngOnInit(): void {
    this.unitsService.list().subscribe({ next: (units) => this.allUnits.set(units) });
    this.taxesService.list().subscribe({ next: (taxes) => this.taxes.set(taxes.filter((t) => t.isActive)) });

    if (this.isEdit()) {
      this.productsService.get(this.productId!).subscribe({
        next: (p) => {
          this.currentBaseUnitId.set(p.baseUnitId);
          this.form.patchValue({
            sku: p.sku,
            name: p.name,
            category: p.category ?? '',
            baseUnitId: p.baseUnitId,
            taxId: p.taxId,
            reorderLevel: p.reorderLevel,
          });
          for (const mode of p.sellingModes) {
            this.sellingModes.push(this.buildModeGroup(mode.id, mode.label, mode.conversionFactorToBase));
          }
        },
      });
    } else {
      this.addMode();
    }
  }

  private buildModeGroup(id: string | null = null, label = '', factor: number | null = null): ModeGroup {
    return this.fb.group({
      id: this.fb.control(id),
      label: this.fb.nonNullable.control(label, Validators.required),
      conversionFactorToBase: this.fb.control(factor, [Validators.required, Validators.min(0.001)]),
    });
  }

  addMode(): void {
    this.sellingModes.push(this.buildModeGroup());
    this.sellingModes.markAsDirty();
  }

  removeMode(index: number): void {
    this.sellingModes.removeAt(index);
    this.sellingModes.markAsDirty();
  }

  submit(): void {
    this.serverError.set(null);

    if (this.sellingModes.length === 0) {
      this.serverError.set('Add at least one selling mode.');
      return;
    }
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const v = this.form.getRawValue();
    const request = {
      sku: v.sku,
      name: v.name,
      category: v.category || null,
      baseUnitId: v.baseUnitId,
      taxId: v.taxId,
      reorderLevel: v.reorderLevel,
      sellingModes: v.sellingModes.map((m) => ({ id: m.id, label: m.label, conversionFactorToBase: m.conversionFactorToBase! })),
    };

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
        const problem = err.error as ApiProblem | undefined;
        if (problem?.errors) {
          for (const [field, messages] of Object.entries(problem.errors)) {
            this.form.get(field.charAt(0).toLowerCase() + field.slice(1))?.setErrors({ server: messages[0] });
          }
          return;
        }
        this.serverError.set(problem?.title ?? 'Something went wrong.');
      },
    });
  }
}
