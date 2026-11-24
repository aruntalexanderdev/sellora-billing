import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { UiCombobox, type ComboOption } from './combobox';

const OPTIONS: ComboOption[] = [
  { value: 's1', label: 'Rice', hint: 'Grains' },
  { value: 's2', label: 'Others', hint: 'Grains' },
  { value: 's3', label: 'Others', hint: 'Dairy' },
];

@Component({
  standalone: true,
  imports: [UiCombobox, ReactiveFormsModule],
  template: `<ui-combobox [formControl]="ctrl" [options]="options" ariaLabel="Subcategory" />`,
})
class Host {
  readonly ctrl = new FormControl('s1', { nonNullable: true });
  readonly options = OPTIONS;
}

function mount() {
  const f = TestBed.createComponent(Host);
  f.detectChanges();
  const el = f.nativeElement as HTMLElement;
  const input = el.querySelector('input')!;
  const combo = f.debugElement.children[0].componentInstance as UiCombobox;
  return { f, el, input, combo, host: f.componentInstance };
}

describe('UiCombobox', () => {
  it('shows the selected label with its hint', () => {
    const { input } = mount();
    expect(input.value).toBe('Rice · Grains');
  });

  it('filters on label and hint, and Enter selects the active option', () => {
    const { f, el, input, combo, host } = mount();
    input.dispatchEvent(new Event('focus'));
    combo.onType('dairy');
    f.detectChanges();
    expect(combo.filtered().map((o) => o.value)).toEqual(['s3']);
    expect(el.querySelectorAll('[role="option"]').length).toBe(1);
    combo.onKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(host.ctrl.value).toBe('s3');
    expect(combo.open()).toBe(false);
  });

  it('offers a None entry that clears the value by mouse', () => {
    const { f, el, combo, host } = mount();
    combo.openList();
    f.detectChanges();
    (el.querySelector('.opt.none') as HTMLElement).click();
    expect(host.ctrl.value).toBe('');
  });

  it('None is reachable from the keyboard', () => {
    const { f, combo, host } = mount();
    combo.openList();
    f.detectChanges();
    expect(combo.activeIndex()).toBe(1); // current value 's1', after "None"
    combo.onKeydown(new KeyboardEvent('keydown', { key: 'ArrowUp' }));
    combo.onKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(host.ctrl.value).toBe('');
  });

  it('Esc closes the list, restores the label and does not let the event reach the document', () => {
    const { f, el, input, host } = mount();
    document.body.appendChild(el); // attached, so an unstopped event would bubble to the document
    host.ctrl.setValue('s2');
    input.dispatchEvent(new Event('focus'));
    input.value = 'xyz';
    input.dispatchEvent(new Event('input'));
    f.detectChanges();
    const reachedDocument = vi.fn();
    document.addEventListener('keydown', reachedDocument);
    const esc = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    input.dispatchEvent(esc);
    document.removeEventListener('keydown', reachedDocument);
    f.detectChanges();
    expect(esc.defaultPrevented).toBe(true);
    expect(reachedDocument).not.toHaveBeenCalled();
    expect(host.ctrl.value).toBe('s2');
    expect(input.value).toBe('Others · Grains');
    el.remove();
  });

  it('follows the form control disabled state', () => {
    const { f, input, host } = mount();
    host.ctrl.disable();
    f.detectChanges();
    expect(input.disabled).toBe(true);
  });
});
