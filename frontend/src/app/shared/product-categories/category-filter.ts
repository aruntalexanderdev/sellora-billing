import { Component, computed, inject, input, model, output, signal } from '@angular/core';
import { ProductCategoriesService } from './product-categories.service';
import type { CategorySelection, ProductCategory } from './product-categories.models';

/**
 * Category -> Subcategory dependent selects (FI-D18). The subcategory select is disabled until a category is
 * chosen; changing the category clears the subcategory. Emits `{ categoryId, subcategoryId }` (null = all).
 * Inactive masters stay listed (marked "(inactive)") so historical products remain filterable.
 */
@Component({
  selector: 'app-category-filter',
  standalone: true,
  template: `
    <select class="field-input" aria-label="Category" [value]="categoryId() ?? ''" (change)="onCategory($any($event.target).value)">
      <option value="">{{ allLabel() }}</option>
      @for (c of categoryOptions(); track c.id) {
        <option [value]="c.id" [selected]="c.id === categoryId()">{{ c.name }}{{ c.isActive ? '' : ' (inactive)' }}</option>
      }
    </select>
    <select
      class="field-input"
      aria-label="Subcategory"
      [disabled]="!categoryId()"
      [value]="subcategoryId() ?? ''"
      (change)="onSubcategory($any($event.target).value)"
    >
      <option value="">All subcategories</option>
      @for (s of subcategoryOptions(); track s.id) {
        <option [value]="s.id" [selected]="s.id === subcategoryId()">{{ s.name }}{{ s.isActive ? '' : ' (inactive)' }}</option>
      }
    </select>
  `,
  styles: [`:host { display: flex; gap: 10px; flex-wrap: wrap; } select { width: auto; min-width: 180px; }`],
})
export class CategoryFilter {
  private readonly service = inject(ProductCategoriesService);

  readonly allLabel = input('All categories');
  readonly categoryId = model<string | null>(null);
  readonly subcategoryId = model<string | null>(null);
  readonly changed = output<CategorySelection>();

  private readonly categories = signal<ProductCategory[]>([]);

  readonly categoryOptions = computed(() => this.categories());
  readonly subcategoryOptions = computed(() => {
    const cat = this.categories().find((c) => c.id === this.categoryId());
    return cat?.subcategories ?? [];
  });

  constructor() {
    this.service.list(true).subscribe({ next: (list) => this.categories.set(list) });
  }

  onCategory(value: string): void {
    const id = value || null;
    if (id === this.categoryId()) return;
    this.categoryId.set(id);
    this.subcategoryId.set(null);
    this.emit();
  }

  onSubcategory(value: string): void {
    const id = value || null;
    if (id === this.subcategoryId()) return;
    this.subcategoryId.set(id);
    this.emit();
  }

  private emit(): void {
    this.changed.emit({ categoryId: this.categoryId(), subcategoryId: this.subcategoryId() });
  }
}
