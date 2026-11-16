import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthService } from '../../../core/auth/auth.service';
import type { ApiProblem } from '../../../core/auth/auth.models';
import { UiBadge } from '../../../shared/ui/badge/badge';
import { UiButton } from '../../../shared/ui/button/button';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ProductCategoriesService } from '../../../shared/product-categories/product-categories.service';
import {
  CODE_MAX_LENGTH,
  CODE_PATTERN,
  suggestCode,
  type ProductCategory,
  type ProductSubcategory,
  type UpsertClassificationRequest,
} from '../../../shared/product-categories/product-categories.models';
import type { Observable } from 'rxjs';

/** What the inline editor is doing: a new/edited category, or a new/edited subcategory under `categoryId`. */
export interface EditorTarget {
  kind: 'category' | 'subcategory';
  categoryId: string | null;
  /** null = create. */
  id: string | null;
}

@Component({
  selector: 'app-product-categories',
  standalone: true,
  imports: [ReactiveFormsModule, UiButton, UiBadge],
  templateUrl: './product-categories.html',
  styleUrl: './product-categories.scss',
})
export class ProductCategories implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(ProductCategoriesService);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmDialogService);

  readonly canManage = computed(() => this.auth.hasPermission('Inventory.Categories'));
  readonly categories = signal<ProductCategory[]>([]);
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly showInactive = signal(false);
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly editor = signal<EditorTarget | null>(null);
  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  /** Once the user edits the code by hand, stop auto-suggesting. */
  private codeTouched = false;

  readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.maxLength(CODE_MAX_LENGTH), Validators.pattern(CODE_PATTERN)]],
    name: ['', [Validators.required, Validators.maxLength(60)]],
    sortOrder: [0, [Validators.required, Validators.min(0)]],
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.service.list(this.showInactive()).subscribe({
      next: (list) => {
        this.categories.set(list);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      },
    });
  }

  setShowInactive(value: boolean): void {
    this.showInactive.set(value);
    this.load();
  }

  isExpanded(id: string): boolean {
    return this.expanded().has(id);
  }

  toggleExpanded(id: string): void {
    const next = new Set(this.expanded());
    if (!next.delete(id)) next.add(id);
    this.expanded.set(next);
  }

  visibleSubcategories(c: ProductCategory): ProductSubcategory[] {
    return this.showInactive() ? c.subcategories : c.subcategories.filter((s) => s.isActive);
  }

  /** Opens the editor; pass an existing row to edit, omit to create. */
  startCategory(c?: ProductCategory): void {
    this.open({ kind: 'category', categoryId: null, id: c?.id ?? null }, c);
  }

  startSubcategory(category: ProductCategory, s?: ProductSubcategory): void {
    this.open({ kind: 'subcategory', categoryId: category.id, id: s?.id ?? null }, s);
    if (!this.isExpanded(category.id)) this.toggleExpanded(category.id);
  }

  private open(target: EditorTarget, existing?: { code: string; name: string; sortOrder: number }): void {
    this.editor.set(target);
    this.serverError.set(null);
    this.codeTouched = !!existing;
    this.form.reset({ code: existing?.code ?? '', name: existing?.name ?? '', sortOrder: existing?.sortOrder ?? 0 });
  }

  cancel(): void {
    this.editor.set(null);
    this.serverError.set(null);
  }

  /** Name typing re-suggests the code until the user edits the code themselves. */
  onNameInput(): void {
    if (this.codeTouched) return;
    this.form.controls.code.setValue(suggestCode(this.form.controls.name.value));
  }

  onCodeInput(): void {
    this.codeTouched = true;
    const c = this.form.controls.code;
    c.setValue(c.value.toUpperCase(), { emitEvent: false });
  }

  submit(): void {
    const target = this.editor();
    if (!target || this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const request: UpsertClassificationRequest = { code: v.code.trim().toUpperCase(), name: v.name.trim(), sortOrder: Number(v.sortOrder) };
    let call$: Observable<unknown>;
    if (target.kind === 'category') {
      call$ = target.id ? this.service.update(target.id, request) : this.service.create(request);
    } else {
      call$ = target.id
        ? this.service.updateSubcategory(target.categoryId!, target.id, request)
        : this.service.createSubcategory(target.categoryId!, request);
    }
    this.serverError.set(null);
    this.saving.set(true);
    call$.subscribe({
      next: () => {
        this.saving.set(false);
        this.toast.show(`${target.kind === 'category' ? 'Category' : 'Subcategory'} ${target.id ? 'updated' : 'added'}.`);
        this.editor.set(null);
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.applyServerErrors(err.error as ApiProblem | undefined);
      },
    });
  }

  /** Maps ProblemDetails `errors` keys (`code`, `name`) onto the controls; anything else goes to `serverError`. */
  applyServerErrors(problem: ApiProblem | undefined): void {
    const unmatched: string[] = [];
    for (const [field, messages] of Object.entries(problem?.errors ?? {})) {
      if (!messages?.length) continue;
      const control = this.form.get(field.charAt(0).toLowerCase() + field.slice(1));
      if (control) {
        control.setErrors({ ...control.errors, server: messages[0] });
        control.markAsTouched();
      } else {
        unmatched.push(messages[0]);
      }
    }
    if (unmatched.length > 0) this.serverError.set(unmatched.join(' '));
    else if (!problem?.errors || Object.keys(problem.errors).length === 0) {
      this.serverError.set(problem?.title ?? 'Could not save.');
    }
  }

  toggleCategory(c: ProductCategory): void {
    this.confirmToggle(
      c.name,
      c.isActive,
      'It will no longer be assignable to products. Products that already use it keep it.',
      (active) => this.service.setActive(c.id, active),
    );
  }

  toggleSubcategory(c: ProductCategory, s: ProductSubcategory): void {
    this.confirmToggle(
      s.name,
      s.isActive,
      'It will no longer be assignable to products. Products that already use it keep it.',
      (active) => this.service.setSubcategoryActive(c.id, s.id, active),
    );
  }

  private confirmToggle(name: string, isActive: boolean, deactivateBody: string, call: (active: boolean) => Observable<unknown>): void {
    this.confirm
      .ask({
        title: isActive ? `Deactivate ${name}?` : `Activate ${name}?`,
        body: isActive ? deactivateBody : 'It can be assigned to products again.',
        confirmLabel: isActive ? 'Deactivate' : 'Activate',
        destructive: isActive,
      })
      .then((confirmed) => {
        if (!confirmed) return;
        call(!isActive).subscribe({
          next: () => {
            this.toast.show(`${name} ${isActive ? 'deactivated' : 'activated'}.`);
            this.load();
          },
          error: (err: HttpErrorResponse) => this.toast.show(err.error?.title ?? 'Could not update status.'),
        });
      });
  }

  fieldError(name: 'code' | 'name' | 'sortOrder'): string | null {
    const c = this.form.controls[name];
    if (!c.touched || !c.errors) return null;
    if (c.errors['server']) return String(c.errors['server']);
    if (name === 'code') return 'Use A-Z, 0-9 and "-", starting with a letter or digit (max 20).';
    if (name === 'name') return 'Name is required (max 60 characters).';
    return 'Enter a sort order of 0 or more.';
  }
}
