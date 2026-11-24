import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { AuthService } from '../../../core/auth/auth.service';
import { UiBadge } from '../../../shared/ui/badge/badge';
import { UiButton } from '../../../shared/ui/button/button';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { ConfirmDialogService } from '../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ProductCategoriesService } from '../../../shared/product-categories/product-categories.service';
import type { ProductCategory, ProductSubcategory } from '../../../shared/product-categories/product-categories.models';
import {
  ClassificationEditorDialog,
  type ClassificationEditorTarget,
} from '../../../shared/product-categories/classification-editor-dialog';
import type { Observable } from 'rxjs';

@Component({
  selector: 'app-product-categories',
  standalone: true,
  imports: [UiButton, UiBadge, ClassificationEditorDialog],
  templateUrl: './product-categories.html',
  styleUrl: './product-categories.scss',
})
export class ProductCategories implements OnInit {
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
  /** Target of the create/edit modal; null = closed. */
  readonly editor = signal<ClassificationEditorTarget | null>(null);

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

  /** Opens the modal; pass an existing row to edit, omit to create. */
  startCategory(c?: ProductCategory): void {
    this.editor.set({ kind: 'category', categoryId: null, id: c?.id ?? null, existing: c });
  }

  startSubcategory(category: ProductCategory, s?: ProductSubcategory): void {
    this.editor.set({ kind: 'subcategory', categoryId: category.id, id: s?.id ?? null, existing: s });
    if (!this.isExpanded(category.id)) this.toggleExpanded(category.id);
  }

  closeEditor(): void {
    this.editor.set(null);
  }

  onSaved(): void {
    this.editor.set(null);
    this.load();
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
}
