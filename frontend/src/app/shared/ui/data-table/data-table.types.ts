import type { BadgeTone } from '../badge/badge';

export type DataTableAlign = 'left' | 'right' | 'center';

export interface DataTableColumn {
  label: string;
  align?: DataTableAlign;
}

export type DataTableCell =
  | { kind: 'text'; text: string; sub?: string; align?: DataTableAlign; weight?: string; color?: string; wrap?: 'nowrap' | 'normal' }
  | { kind: 'badge'; text: string; tone: BadgeTone; align?: DataTableAlign }
  | { kind: 'link'; text: string; onClick: () => void; align?: DataTableAlign }
  | { kind: 'actions'; actions: DataTableRowAction[]; align?: DataTableAlign };

export interface DataTableRowAction {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  title?: string;
}

export interface DataTableRow {
  cells: DataTableCell[];
}

export interface DataTableFilter {
  value: string;
  options: string[];
  onChange: (value: string) => void;
}

export interface DataTableToolbarAction {
  label: string;
  onClick: () => void;
}

export type DataTableState = 'ready' | 'loading' | 'error';
