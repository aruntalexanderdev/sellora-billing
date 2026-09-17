import { inject } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { ConfirmDialogService } from '../shared/ui/confirm-dialog/confirm-dialog.service';

export interface HasUnsavedChanges {
  hasUnsavedChanges(): boolean;
}

export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = (component) => {
  if (!component.hasUnsavedChanges()) return true;

  const confirm = inject(ConfirmDialogService);
  return confirm.ask({
    title: 'Leave without saving?',
    body: 'You have unsaved changes. If you leave now, they will be lost.',
    confirmLabel: 'Leave',
    cancelLabel: 'Stay',
    destructive: true,
  });
};
