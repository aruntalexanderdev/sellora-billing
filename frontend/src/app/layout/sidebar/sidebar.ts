import { Component, input, output } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import type { SidebarNavGroup } from './sidebar.types';

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './sidebar.html',
  styleUrl: './sidebar.scss',
  host: {
    '[class.sb-open]': 'open()',
  },
})
export class Sidebar {
  readonly navGroups = input.required<SidebarNavGroup[]>();
  readonly open = input(false);
  readonly closeRequested = output<void>();
}
