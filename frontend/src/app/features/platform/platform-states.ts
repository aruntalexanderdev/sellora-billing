import { Component, inject } from '@angular/core';
import { GeoStatesScreen } from '../../shared/geo/geo-states-screen';
import { PlatformService } from './platform.service';

/** Platform-managed states: always writable here (D8). */
@Component({
  selector: 'app-platform-states',
  standalone: true,
  imports: [GeoStatesScreen],
  template: `<app-geo-states-screen [api]="api" />`,
})
export class PlatformStates {
  readonly api = inject(PlatformService);
}
