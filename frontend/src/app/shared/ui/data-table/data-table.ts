import { Component, computed, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { UiBadge } from '../badge/badge';
import type {
  DataTableColumn,
  DataTableFilter,
  DataTableRow,
  DataTableState,
  DataTableToolbarAction,
} from './data-table.types';

@Component({
  selector: 'ui-data-table',
  standalone: true,
  imports: [FormsModule, UiBadge],
  templateUrl: './data-table.html',
  styleUrl: './data-table.scss',
})
export class UiDataTable {
  readonly cols = input.required<DataTableColumn[]>();
  readonly rows = input.required<DataTableRow[]>();
  readonly state = input<DataTableState>('ready');
  readonly density = input<'comfortable' | 'compact'>('comfortable');

  readonly showSearch = input(false);
  readonly searchValue = input('');
  readonly searchPlaceholder = input('Search…');
  readonly searchChange = output<string>();

  readonly filters = input<DataTableFilter[]>([]);
  readonly toolbarActions = input<DataTableToolbarAction[]>([]);

  readonly emptyTitle = input('Nothing here yet');
  readonly emptyBody = input('Records will appear here once they are created.');
  readonly emptyAction = input<string | null>(null);
  readonly emptyActionClick = output<void>();

  readonly retry = output<void>();
  readonly footerText = input('');

  readonly page = input(1);
  readonly hasMore = input(false);
  readonly pageChange = output<number>();
  readonly showPagination = computed(() => this.page() > 1 || this.hasMore());

  readonly rowPad = computed(() => (this.density() === 'compact' ? '9px' : '13px'));
  readonly isLoading = computed(() => this.state() === 'loading');
  readonly isError = computed(() => this.state() === 'error');
  readonly isEmpty = computed(() => this.state() === 'ready' && this.rows().length === 0);
  readonly isReady = computed(() => this.state() === 'ready' && this.rows().length > 0);
  readonly hasToolbar = computed(
    () => this.showSearch() || this.filters().length > 0 || this.toolbarActions().length > 0,
  );
  readonly skeletonRows = [1, 2, 3, 4, 5];

  onSearchInput(value: string): void {
    this.searchChange.emit(value);
  }

  goPrevPage(): void {
    if (this.page() > 1) this.pageChange.emit(this.page() - 1);
  }

  goNextPage(): void {
    if (this.hasMore()) this.pageChange.emit(this.page() + 1);
  }
}
