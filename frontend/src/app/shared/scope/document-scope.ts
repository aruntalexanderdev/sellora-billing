import { Component, input } from '@angular/core';

export interface DocumentScopeItem {
  label: string;
  value: string;
  testId: string;
}

/** Read-only strip of document scope facts (branch, location, seller GSTIN) shown under a detail header. */
@Component({
  selector: 'app-document-scope',
  standalone: true,
  template: `
    <div class="doc-scope" data-testid="document-scope">
      @for (item of items(); track item.testId) {
        <div>
          <span class="doc-scope-label">{{ item.label }}</span>
          <strong [attr.data-testid]="item.testId">{{ item.value }}</strong>
        </div>
      }
    </div>
  `,
  styles: `
    .doc-scope {
      display: flex;
      flex-wrap: wrap;
      gap: 8px 28px;
      margin-bottom: 14px;
      padding: 10px 16px;
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      border-radius: var(--radius-card);
      font-size: 13px;
    }
    .doc-scope-label {
      display: block;
      font-size: 11px;
      font-weight: 650;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: var(--color-text-fainter);
    }
  `,
})
export class DocumentScope {
  readonly items = input.required<DocumentScopeItem[]>();
}
