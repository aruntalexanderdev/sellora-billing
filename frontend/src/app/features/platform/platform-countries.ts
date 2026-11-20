import { Component, inject } from '@angular/core';
import { GeoCountriesScreen } from '../../shared/geo/geo-countries-screen';
import { PlatformService } from './platform.service';

/** Platform-managed countries: always writable here (D8); company screens follow the guard. */
@Component({
  selector: 'app-platform-countries',
  standalone: true,
  imports: [GeoCountriesScreen],
  template: `<app-geo-countries-screen [api]="api" />`,
})
export class PlatformCountries {
  readonly api = inject(PlatformService);
}
