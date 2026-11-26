import { Component, ElementRef, afterRenderEffect, computed, forwardRef, input, signal, viewChild } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';

export interface ComboOption {
  value: string;
  label: string;
  /** Secondary text shown after the label and included in the search (e.g. the parent category). */
  hint?: string;
}

let nextId = 0;

/**
 * Searchable single-select (type-to-filter) for reactive forms. Value is a string; `''` means none.
 * Keyboard: ↓/↑ move, Enter selects, Esc closes and restores the selection's label.
 */
@Component({
  selector: 'ui-combobox',
  standalone: true,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => UiCombobox), multi: true }],
  template: `
    <div class="wrap">
      <input
        #input
        type="text"
        class="field-input"
        role="combobox"
        autocomplete="off"
        [id]="inputId()"
        [attr.aria-label]="ariaLabel()"
        [attr.aria-expanded]="open()"
        [attr.aria-controls]="listId"
        aria-autocomplete="list"
        [attr.aria-activedescendant]="open() && activeIndex() >= 0 ? listId + '-' + activeIndex() : null"
        [placeholder]="open() && selectedText() ? selectedText() : placeholder()"
        [disabled]="disabled()"
        [value]="open() ? query() : selectedText()"
        (focus)="openList()"
        (click)="openList()"
        (input)="onType($any($event.target).value)"
        (keydown)="onKeydown($event)"
        (blur)="onBlur()"
      />
      @if (open()) {
        <!-- mousedown is prevented on the whole list so scrolling it does not blur the input. -->
        <ul #list class="list" role="listbox" [id]="listId" [attr.aria-label]="ariaLabel()" (mousedown)="$event.preventDefault()">
          @for (o of entries(); track o.value; let i = $index) {
            <li
              role="option"
              class="opt"
              [class.none]="o.value === ''"
              [id]="listId + '-' + i"
              [class.active]="i === activeIndex()"
              [attr.aria-selected]="o.value === value()"
              (click)="select(o.value)"
              (mouseenter)="activeIndex.set(i)"
            >{{ o.label }}@if (o.hint) {<span class="hint"> · {{ o.hint }}</span>}</li>
          } @empty {
            <li class="opt empty" aria-disabled="true">No matches</li>
          }
        </ul>
      }
    </div>
  `,
  styles: [`
    @use '../../styles/form-fields';
    :host { display: block; }
    .wrap { position: relative; }
    .list {
      position: absolute;
      z-index: 20;
      top: calc(100% + 4px);
      left: 0;
      right: 0;
      max-height: 260px;
      overflow-y: auto;
      margin: 0;
      padding: 4px;
      list-style: none;
      background: var(--color-surface);
      border: 1px solid var(--color-border-strong);
      border-radius: var(--radius-control);
      box-shadow: 0 10px 24px rgba(12, 32, 36, .12);
    }
    .opt {
      padding: 7px 9px;
      border-radius: var(--radius-sm);
      font-size: 13.5px;
      cursor: pointer;
    }
    .opt.active, .opt[aria-selected='true'] { background: var(--color-accent-soft-bg); }
    .opt.none, .hint { color: var(--color-text-muted); }
    .opt.empty { color: var(--color-text-muted); cursor: default; }
  `],
})
export class UiCombobox implements ControlValueAccessor {
  readonly options = input<ComboOption[]>([]);
  readonly placeholder = input('');
  readonly ariaLabel = input<string | null>(null);
  readonly inputId = input<string | null>(null);
  /** Label of the "no selection" entry; null hides it (selection then cannot be cleared from the list). */
  readonly emptyLabel = input<string | null>('None');

  readonly listId = `ui-combobox-${++nextId}`;
  readonly value = signal('');
  readonly disabled = signal(false);
  readonly open = signal(false);
  readonly query = signal('');
  readonly activeIndex = signal(-1);
  private readonly inputEl = viewChild.required<ElementRef<HTMLInputElement>>('input');
  private readonly listEl = viewChild<ElementRef<HTMLUListElement>>('list');

  readonly selectedText = computed(() => {
    const o = this.options().find((x) => x.value === this.value());
    return o ? (o.hint ? `${o.label} · ${o.hint}` : o.label) : '';
  });

  readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    if (!q) return this.options();
    return this.options().filter((o) => `${o.label} ${o.hint ?? ''}`.toLowerCase().includes(q));
  });

  /** What the open list shows: the "None" entry (only without a query) followed by the matches. */
  readonly entries = computed<ComboOption[]>(() => {
    const none = this.emptyLabel();
    return none !== null && !this.query() ? [{ value: '', label: none }, ...this.filtered()] : this.filtered();
  });

  constructor() {
    // Keep the active option in view while moving with the arrow keys.
    afterRenderEffect(() => {
      const i = this.activeIndex();
      const list = this.listEl()?.nativeElement;
      if (!list || i < 0) return;
      list.querySelector<HTMLElement>(`#${this.listId}-${i}`)?.scrollIntoView?.({ block: 'nearest' });
    });
  }

  private onChange: (v: string) => void = () => {};
  private onTouched: () => void = () => {};

  writeValue(v: string | null): void {
    this.value.set(v ?? '');
  }
  registerOnChange(fn: (v: string) => void): void {
    this.onChange = fn;
  }
  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }
  setDisabledState(isDisabled: boolean): void {
    this.disabled.set(isDisabled);
  }

  openList(): void {
    if (this.open() || this.disabled()) return;
    this.query.set('');
    this.activeIndex.set(this.entries().findIndex((o) => o.value === this.value()));
    this.open.set(true);
  }

  onType(text: string): void {
    this.query.set(text);
    this.open.set(true);
    this.activeIndex.set(this.entries().length ? 0 : -1);
  }

  select(v: string): void {
    this.open.set(false);
    this.query.set('');
    if (v !== this.value()) {
      this.value.set(v);
      this.onChange(v);
    }
    this.onTouched();
  }

  onKeydown(e: KeyboardEvent): void {
    const count = this.entries().length;
    // Options can change while the list is open; never point past the end.
    if (this.activeIndex() >= count) this.activeIndex.set(count - 1);
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        if (!this.open()) this.openList();
        else if (count) this.activeIndex.set((this.activeIndex() + 1) % count);
        break;
      case 'ArrowUp':
        e.preventDefault();
        if (count) this.activeIndex.set(this.activeIndex() <= 0 ? count - 1 : this.activeIndex() - 1);
        break;
      case 'Enter':
        if (this.open()) {
          e.preventDefault();
          const o = this.entries()[this.activeIndex()];
          if (o) this.select(o.value);
        }
        break;
      case 'Escape':
        if (this.open()) {
          // Do not let a surrounding modal close as well.
          e.preventDefault();
          e.stopPropagation();
          this.close();
        }
        break;
      case 'Tab':
        this.close();
        break;
    }
  }

  onBlur(): void {
    this.close();
    this.onTouched();
  }

  focus(): void {
    this.inputEl().nativeElement.focus();
  }

  private close(): void {
    this.open.set(false);
    this.query.set('');
  }
}
