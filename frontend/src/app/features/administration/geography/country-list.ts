import { Component, computed, inject } from '@angular/core';
import { AuthService } from '../../../core/auth/auth.service';
import { GeoCountriesScreen } from '../../../shared/geo/geo-countries-screen';
import { GeoService } from '../../../shared/geo/geo.service';

/** Administration -> Countries. Read-only unless the server reports canEdit (D8) and the user holds Masters.Edit. */
@Component({
  selector: 'app-country-list',
  standalone: true,
  imports: [GeoCountriesScreen],
  template: `<app-geo-countries-screen [api]="api" [allowWrite]="allowWrite()" />`,
})
export class CountryList {
  readonly api = inject(GeoService);
  private readonly auth = inject(AuthService);
  readonly allowWrite = computed(() => this.auth.hasPermission('Masters.Edit'));
}
