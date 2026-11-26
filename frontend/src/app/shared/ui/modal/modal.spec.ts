import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { UiModal } from './modal';
import { ConfirmDialogService } from '../confirm-dialog/confirm-dialog.service';

@Component({
  standalone: true,
  imports: [UiModal],
  template: `
    <button id="opener" type="button">Open</button>
    @if (open()) {
      <ui-modal title="New category" (closed)="closedCount = closedCount + 1">
        <input id="first" />
        <input id="last" />
        <div modal-footer><button id="save" type="button">Save</button></div>
      </ui-modal>
    }
  `,
})
class Host {
  readonly open = signal(false);
  closedCount = 0;
}

const attached: HTMLElement[] = [];

function mount() {
  const f = TestBed.createComponent(Host);
  // Attached so focus() works.
  document.body.appendChild(f.nativeElement);
  attached.push(f.nativeElement);
  f.detectChanges();
  const el = f.nativeElement as HTMLElement;
  const opener = el.querySelector<HTMLButtonElement>('#opener')!;
  opener.focus();
  f.componentInstance.open.set(true);
  f.detectChanges();
  return { f, el, host: f.componentInstance, opener };
}

const key = (k: string, init: KeyboardEventInit = {}) => {
  const e = new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true, ...init });
  document.dispatchEvent(e);
  return e;
};

describe('UiModal', () => {
  afterEach(() => attached.splice(0).forEach((n) => n.remove()));

  it('renders an accessible dialog with title, content and footer', () => {
    const { el } = mount();
    const dialog = el.querySelector('[role="dialog"]')!;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    const titleId = dialog.getAttribute('aria-labelledby')!;
    expect(el.querySelector(`#${titleId}`)?.textContent).toBe('New category');
    expect(el.querySelector('.footer #save')).not.toBeNull();
  });

  it('moves focus to the first field on open and restores it on close', () => {
    const { f, el, host, opener } = mount();
    expect(document.activeElement).toBe(el.querySelector('#first'));
    host.open.set(false);
    f.detectChanges();
    expect(document.activeElement).toBe(opener);
  });

  it('emits closed on Esc and on backdrop click, not on a click inside the panel', () => {
    const { el, host } = mount();
    key('Escape');
    expect(host.closedCount).toBe(1);
    (el.querySelector('.panel') as HTMLElement).click();
    expect(host.closedCount).toBe(1);
    const backdrop = el.querySelector('.backdrop') as HTMLElement;
    backdrop.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    backdrop.click();
    expect(host.closedCount).toBe(2);
  });

  it('ignores a drag that starts inside the panel and ends on the backdrop', () => {
    const { el, host } = mount();
    el.querySelector('#first')!.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    (el.querySelector('.backdrop') as HTMLElement).click();
    expect(host.closedCount).toBe(0);
  });

  it('ignores Esc already handled by an inner control or while a confirm dialog is on top', () => {
    const { el, host } = mount();
    const input = el.querySelector('#first')!;
    input.addEventListener('keydown', (e) => e.preventDefault(), { once: true });
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    expect(host.closedCount).toBe(0);

    void TestBed.inject(ConfirmDialogService).ask({ title: 'Sure?', body: '' });
    key('Escape');
    expect(host.closedCount).toBe(0);
    TestBed.inject(ConfirmDialogService).respond(false);
    key('Escape');
    expect(host.closedCount).toBe(1);
  });

  it('traps Tab inside the panel', () => {
    const { el } = mount();
    const save = el.querySelector<HTMLElement>('#save')!;
    save.focus();
    const e = key('Tab');
    expect(e.defaultPrevented).toBe(true);
    // Wraps to the first focusable element (the header close button).
    expect(document.activeElement).toBe(el.querySelector('.close'));
    key('Tab', { shiftKey: true });
    expect(document.activeElement).toBe(save);
  });
});
