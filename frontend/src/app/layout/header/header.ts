import { Component, input, output } from '@angular/core';

@Component({
  selector: 'app-header',
  standalone: true,
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
}
