import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { UiBranchPickerHost } from './shared/ui/branch-picker/branch-picker-host';

// The branch picker lives at the root, outside the router outlet, so a requiresBranch guard can open it during the
// initial navigation (deep link / reload) before the shell has been activated.
@Component({
  imports: [RouterOutlet, UiBranchPickerHost],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {}
