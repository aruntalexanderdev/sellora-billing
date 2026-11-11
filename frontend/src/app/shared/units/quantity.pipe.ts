import { Pipe, PipeTransform } from '@angular/core';
import { formatQuantity } from './unit-conversion';

/** `{{ qty | quantity: decimalPlaces }}` — precision-aware quantity text (widens, never hides, stored precision). */
@Pipe({ name: 'quantity', standalone: true })
export class QuantityPipe implements PipeTransform {
  transform(value: number | null | undefined, dp: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return formatQuantity(value, dp);
  }
}
