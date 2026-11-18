import { Injectable, effect, inject } from '@angular/core';
import { ContextService } from '../../../core/context/context.service';
import { Observable, shareReplay } from 'rxjs';
import { map } from 'rxjs/operators';
import { ProductsService } from '../products/products.service';
import type { ProductUnit } from '../products/products.models';
import type { UnitRef } from './stock-transfer-rules';

/** Cached per-product unit lists (all units, base first as returned by the API) for transfer line pickers and dialogs. */
@Injectable({ providedIn: 'root' })
export class ProductUnitsLookup {
  private readonly products = inject(ProductsService);
  private readonly cache = new Map<string, Observable<ProductUnit[]>>();

  constructor() {
    // Branch/permission context changed: drop cached lookups so nothing is served across branches.
    const context = inject(ContextService);
    effect(() => {
      context.contextVersion();
      this.cache.clear();
    });
  }

  units(productId: string): Observable<ProductUnit[]> {
    let cached = this.cache.get(productId);
    if (!cached) {
      cached = this.products.get(productId).pipe(
        map((p) => p.units),
        shareReplay({ bufferSize: 1, refCount: false }),
      );
      this.cache.set(productId, cached);
    }
    return cached;
  }
}

/** Active units of a product, plus the unit a document already uses even if it was deactivated since. */
export function toUnitRefs(units: readonly ProductUnit[], keep: readonly (string | null | undefined)[] = []): UnitRef[] {
  return units
    .filter((u) => u.isActive || keep.includes(u.id))
    .map((u) => ({ id: u.id, unitCode: u.unitCode, displayName: u.displayName, decimalPlaces: u.decimalPlaces, conversionFactorToBase: u.conversionFactorToBase }));
}
