import { Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-header',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './header.html',
  styleUrl: './header.scss',
})
export class Header {
  readonly crumbGroup = input('');
  readonly crumbPage = input('');
  readonly userName = input('');
  readonly userRole = input('');
  readonly userInitials = input('');
  readonly signOut = output<void>();
  readonly menuToggle = output<void>();
}
