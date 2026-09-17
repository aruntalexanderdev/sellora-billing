import { Routes } from '@angular/router';
import { Shell } from './layout/shell/shell';
import { UiShowcase } from './features/ui-showcase/ui-showcase';

export const routes: Routes = [
  {
    path: '',
    component: Shell,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'ui-showcase' },
      { path: 'ui-showcase', component: UiShowcase },
    ],
  },
];
