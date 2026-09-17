import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { RolesService } from '../roles.service';
import { PermissionsService } from '../../permissions/permissions.service';
import type { PermissionCatalogEntry } from '../../permissions/permissions.models';
import type { ApiProblem } from '../../../../core/auth/auth.models';
import type { HasUnsavedChanges } from '../../../../core/unsaved-changes.guard';

interface FeatureGroup {
  feature: string;
  entries: PermissionCatalogEntry[];
}

interface ModuleSection {
  module: string;
  features: FeatureGroup[];
}

function featureOf(entry: PermissionCatalogEntry): string {
  return entry.code.split('.')[0];
}

@Component({
  selector: 'app-role-form',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, UiButton],
  templateUrl: './role-form.html',
  styleUrl: './role-form.scss',
})
export class RoleForm implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly rolesService = inject(RolesService);
  private readonly permissionsService = inject(PermissionsService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly roleId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.roleId !== null && this.roleId !== 'new');

  readonly form = this.fb.nonNullable.group({
    name: ['', Validators.required],
    description: [''],
  });

  readonly catalog = signal<PermissionCatalogEntry[]>([]);
  readonly selected = signal<Set<string>>(new Set());
  private initialSelected = new Set<string>();
  private saved = false;

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);

  readonly moduleSections = computed<ModuleSection[]>(() => {
    const byModule = new Map<string, Map<string, PermissionCatalogEntry[]>>();
    for (const entry of this.catalog()) {
      const byFeature = byModule.get(entry.module) ?? new Map<string, PermissionCatalogEntry[]>();
      const list = byFeature.get(featureOf(entry)) ?? [];
      list.push(entry);
      byFeature.set(featureOf(entry), list);
      byModule.set(entry.module, byFeature);
    }
    return Array.from(byModule.entries()).map(([module, byFeature]) => ({
      module,
      features: Array.from(byFeature.entries()).map(([feature, entries]) => ({ feature, entries })),
    }));
  });

  readonly totalCount = computed(() => this.catalog().length);
  readonly selectedCount = computed(() => this.selected().size);

  hasUnsavedChanges(): boolean {
    if (this.saved) return false;
    if (this.form.dirty) return true;
    return !setsEqual(this.selected(), this.initialSelected);
  }

  ngOnInit(): void {
    this.permissionsService.list().subscribe({ next: (list) => this.catalog.set(list) });

    if (this.isEdit()) {
      this.rolesService.get(this.roleId!).subscribe({
        next: (role) => {
          this.form.patchValue({ name: role.name, description: role.description ?? '' });
          const set = new Set(role.permissionCodes);
          this.selected.set(set);
          this.initialSelected = new Set(set);
        },
      });
    }
  }

  isChecked(code: string): boolean {
    return this.selected().has(code);
  }

  toggle(code: string): void {
    const next = new Set(this.selected());
    if (next.has(code)) next.delete(code);
    else next.add(code);
    this.selected.set(next);
  }

  isFeatureFullySelected(group: FeatureGroup): boolean {
    return group.entries.every((e) => this.isChecked(e.code));
  }

  toggleFeature(group: FeatureGroup): void {
    const allSelected = this.isFeatureFullySelected(group);
    const next = new Set(this.selected());
    for (const entry of group.entries) {
      if (allSelected) next.delete(entry.code);
      else next.add(entry.code);
    }
    this.selected.set(next);
  }

  submit(): void {
    this.serverError.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const v = this.form.getRawValue();
    const request = { name: v.name, description: v.description || null, permissionCodes: Array.from(this.selected()) };

    const request$ = this.isEdit() ? this.rolesService.update(this.roleId!, request) : this.rolesService.create(request);

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Role updated.' : 'Role created.');
        this.router.navigateByUrl('/administration/roles');
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        const problem = err.error as ApiProblem | undefined;
        this.serverError.set(problem?.title ?? Object.values(problem?.errors ?? {})[0]?.[0] ?? 'Something went wrong.');
      },
    });
  }
}

function setsEqual(a: Set<string>, b: Set<string>): boolean {
  if (a.size !== b.size) return false;
  for (const item of a) if (!b.has(item)) return false;
  return true;
}
