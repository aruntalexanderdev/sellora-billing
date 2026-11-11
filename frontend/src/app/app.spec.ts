import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { BranchPickerService } from './shared/ui/branch-picker/branch-picker.service';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });
});

describe('App root hosts', () => {
  it('mounts the branch picker host outside the router outlet so it renders before any route activates', async () => {
    await TestBed.configureTestingModule({ imports: [App], providers: [provideRouter([])] }).compileComponents();
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    TestBed.inject(BranchPickerService).pick([{ id: 'b1', name: 'Main Branch' } as never]);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="dialog"]')?.textContent).toContain('Choose a branch');
  });
});
