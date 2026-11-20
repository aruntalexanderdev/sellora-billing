import { Component, computed, inject } from '@angular/core';
import { AuthService } from '../../../core/auth/auth.service';
import { GeoStatesScreen } from '../../../shared/geo/geo-states-screen';
import { GeoService } from '../../../shared/geo/geo.service';

/** Administration -> States. Read-only unless the server reports canEdit (D8) and the user holds Masters.Edit. */
@Component({
  selector: 'app-state-list',
  standalone: true,
  imports: [GeoStatesScreen],
  template: `<app-geo-states-screen [api]="api" [allowWrite]="allowWrite()" />`,
})
export class StateList {
  readonly api = inject(GeoService);
  private readonly auth = inject(AuthService);
  readonly allowWrite = computed(() => this.auth.hasPermission('Masters.Edit'));
}
