import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { map, type Observable } from 'rxjs';
import type { ApiProblem } from '../../core/auth/auth.models';
import { UiButton } from '../ui/button/button';
import { UiModal } from '../ui/modal/modal';
import { ToastService } from '../ui/toast/toast.service';
import { ProductCategoriesService } from './product-categories.service';
import {
  CODE_MAX_LENGTH,
  CODE_PATTERN,
  suggestCode,
  type ProductCategory,
  type ProductSubcategory,
  type UpsertClassificationRequest,
} from './product-categories.models';

/**
 * What the editor works on: a category, or a subcategory under `categoryId`.
 * `id` null = create. A subcategory create with `categoryId` null shows a category picker.
 */
export interface ClassificationEditorTarget {
  kind: 'category' | 'subcategory';
  categoryId: string | null;
  id: string | null;
  existing?: { code: string; name: string; sortOrder: number };
}

export type ClassificationSaved =
  | { kind: 'category'; created: boolean; category: ProductCategory }
  | { kind: 'subcategory'; created: boolean; subcategory: ProductSubcategory };

/** Create/edit modal for product categories and subcategories (code auto-suggest, server-error mapping). */
@Component({
  selector: 'app-classification-editor-dialog',
  standalone: true,
  imports: [ReactiveFormsModule, UiModal, UiButton],
  template: `
    <form [formGroup]="form" (ngSubmit)="submit()" aria-label="Category editor" novalidate>
      <ui-modal [title]="title()" (closed)="closed.emit()">
        @if (target().kind === 'subcategory') {
          @if (showCategoryPicker()) {
            <div class="row">
              <label class="field-label" for="ce-category">Category</label>
              <select id="ce-category" formControlName="categoryId" class="field-input">
                <option value="" disabled>Select a category…</option>
                @for (c of pickableCategories(); track c.id) {
                  <option [value]="c.id">{{ c.name }}</option>
                }
              </select>
              @if (fieldError('categoryId'); as msg) { <div class="field-error">{{ msg }}</div> }
            </div>
          } @else {
            <p class="context">Under category: <strong>{{ contextCategoryName() }}</strong></p>
          }
        }
        <div class="row">
          <label class="field-label" for="ce-name">Name</label>
          <input id="ce-name" type="text" formControlName="name" class="field-input" maxlength="60" (input)="onNameInput()" />
          @if (fieldError('name'); as msg) { <div class="field-error">{{ msg }}</div> }
        </div>
        <div class="grid">
          <div>
            <label class="field-label" for="ce-code">Code</label>
            <input id="ce-code" type="text" formControlName="code" class="field-input" maxlength="20" (input)="onCodeInput()" />
            @if (fieldError('code'); as msg) { <div class="field-error">{{ msg }}</div> }
          </div>
          <div>
            <label class="field-label" for="ce-sort">Sort order</label>
            <input id="ce-sort" type="number" min="0" step="1" formControlName="sortOrder" class="field-input" />
            @if (fieldError('sortOrder'); as msg) { <div class="field-error">{{ msg }}</div> }
          </div>
        </div>
        @if (serverError()) { <div class="field-error">{{ serverError() }}</div> }
        <div modal-footer>
          <ui-button variant="secondary" (onClick)="closed.emit()">Cancel</ui-button>
          <ui-button type="submit" variant="primary" [disabled]="saving()">{{ saving() ? 'Saving…' : 'Save' }}</ui-button>
        </div>
      </ui-modal>
    </form>
  `,
  styles: [`
    @use '../styles/form-fields';
    .row { margin-bottom: 14px; }
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
      gap: 14px;
      margin-bottom: 8px;
    }
    .context {
      margin: 0 0 14px;
      padding: 9px 11px;
      border-radius: var(--radius-control);
      background: var(--color-accent-soft-bg);
      color: var(--color-accent-soft-fg);
      font-size: 13px;
    }
  `],
})
export class ClassificationEditorDialog implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly service = inject(ProductCategoriesService);
  private readonly toast = inject(ToastService);

  readonly target = input.required<ClassificationEditorTarget>();
  /** Categories for the context line and the picker. */
  readonly categories = input<ProductCategory[]>([]);
  readonly saved = output<ClassificationSaved>();
  readonly closed = output<void>();

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);
  /** Once the user edits the code by hand, stop auto-suggesting. */
  private codeTouched = false;

  readonly form = this.fb.nonNullable.group({
    categoryId: [''],
    code: ['', [Validators.required, Validators.maxLength(CODE_MAX_LENGTH), Validators.pattern(CODE_PATTERN)]],
    name: ['', [Validators.required, Validators.maxLength(60)]],
    sortOrder: [0, [Validators.required, Validators.min(0)]],
  });

  readonly showCategoryPicker = computed(() => this.target().kind === 'subcategory' && !this.target().categoryId);
  readonly pickableCategories = computed(() => this.categories().filter((c) => c.isActive));
  readonly contextCategoryName = computed(() => this.categories().find((c) => c.id === this.target().categoryId)?.name ?? '');
  readonly title = computed(() => {
    const t = this.target();
    return `${t.id ? 'Edit' : 'New'} ${t.kind}`;
  });

  ngOnInit(): void {
    const t = this.target();
    this.codeTouched = !!t.existing;
    this.form.reset({
      categoryId: t.categoryId ?? '',
      code: t.existing?.code ?? '',
      name: t.existing?.name ?? '',
      sortOrder: t.existing?.sortOrder ?? 0,
    });
    if (this.showCategoryPicker()) this.form.controls.categoryId.setValidators(Validators.required);
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
    if (this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const t = this.target();
    const v = this.form.getRawValue();
    const request: UpsertClassificationRequest = { code: v.code.trim().toUpperCase(), name: v.name.trim(), sortOrder: Number(v.sortOrder) };
    const created = !t.id;
    let call$: Observable<ClassificationSaved>;
    if (t.kind === 'category') {
      const res$ = t.id ? this.service.update(t.id, request) : this.service.create(request);
      call$ = res$.pipe(map((category): ClassificationSaved => ({ kind: 'category', created, category })));
    } else {
      const categoryId = v.categoryId;
      const res$ = t.id ? this.service.updateSubcategory(categoryId, t.id, request) : this.service.createSubcategory(categoryId, request);
      call$ = res$.pipe(map((subcategory): ClassificationSaved => ({ kind: 'subcategory', created, subcategory })));
    }
    this.serverError.set(null);
    this.saving.set(true);
    call$.subscribe({
      next: (result) => {
        this.saving.set(false);
        this.toast.show(`${t.kind === 'category' ? 'Category' : 'Subcategory'} ${created ? 'added' : 'updated'}.`);
        this.saved.emit(result);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.applyServerErrors(err.error as ApiProblem | undefined);
      },
    });
  }

  /** Maps ProblemDetails `errors` keys (`code`, `name`, …) onto the controls; anything else goes to `serverError`. */
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

  fieldError(name: 'categoryId' | 'code' | 'name' | 'sortOrder'): string | null {
    const c = this.form.controls[name];
    if (!c.touched || !c.errors) return null;
    if (c.errors['server']) return String(c.errors['server']);
    if (name === 'categoryId') return 'Select a category.';
    if (name === 'code') return 'Use A-Z, 0-9 and "-", starting with a letter or digit (max 20).';
    if (name === 'name') return 'Name is required (max 60 characters).';
    return 'Enter a sort order of 0 or more.';
  }
}

