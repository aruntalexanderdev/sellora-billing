/** A CSV cell: text, a number (written as is, no grouping) or empty. */
export type CsvCell = string | number | boolean | null | undefined;

/** RFC 4180 escaping: quote when the cell holds a comma, quote, CR or LF; double inner quotes. A leading `=+-@` is neutralised against spreadsheet formula injection (text only; numbers are untouched). */
export function csvEscape(cell: CsvCell): string {
  if (cell === null || cell === undefined) return '';
  let text = String(cell);
  // Leading spaces do not stop a spreadsheet from evaluating "  =cmd()", so the first non-space character decides.
  if (typeof cell === 'string' && /^[ ]*[=+\-@\t\r]/.test(text) && !/^-?\d+(\.\d+)?$/.test(text)) text = `'${text}`;
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

/** The CSV text of a header row plus data rows, CRLF separated, without a BOM. */
export function toCsv(header: readonly CsvCell[], rows: readonly (readonly CsvCell[])[]): string {
  return [header, ...rows].map((r) => r.map(csvEscape).join(',')).join('\r\n');
}

/** UTF-8 byte order mark so Excel opens the file as UTF-8. */
export const CSV_BOM = '﻿';

/** Blob of the CSV with the UTF-8 BOM (Excel). */
export function csvBlob(header: readonly CsvCell[], rows: readonly (readonly CsvCell[])[]): Blob {
  return new Blob([CSV_BOM + toCsv(header, rows)], { type: 'text/csv;charset=utf-8' });
}

/** Downloads a CSV through a temporary link (browser only). */
export function downloadCsv(fileName: string, header: readonly CsvCell[], rows: readonly (readonly CsvCell[])[]): void {
  const url = URL.createObjectURL(csvBlob(header, rows));
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}
