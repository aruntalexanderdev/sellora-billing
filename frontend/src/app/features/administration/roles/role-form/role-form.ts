import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthService } from '../../../../core/auth/auth.service';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiButton } from '../../../../shared/ui/button/button';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { RolesService } from '../roles.service';
import { PermissionsService } from '../../permissions/permissions.service';
import type { PermissionCatalogEntry } from '../../permissions/permissions.models';
import { applyProblem, toProblem } from '../../../../core/http/api-errors';
import type { AccessibleBranch } from '../../../../shared/branches/branch-select';
import { UserAccessService } from '../../users/user-access/user-access.service';
import { buildCreateRoleRequest, buildUpdateRoleRequest, eligibleCatalog, holdsBranchAdminCode, pruneSelection, withoutBranchAdminCodes } from '../roles.logic';
import { ContextService } from '../../../../core/context/context.service';
import type { RoleScope } from '../roles.models';
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
  private readonly accessService = inject(UserAccessService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly auth = inject(AuthService);
  private readonly context = inject(ContextService);

  readonly roleId = this.route.snapshot.paramMap.get('id');
  readonly isEdit = computed(() => this.roleId !== null && this.roleId !== 'new');

  /** Per action (FI-D45): create without Roles.Create, or edit without Roles.Edit, is the branch path (Branch.Roles.Manage). */
  readonly branchOnly = !this.auth.hasPermission(this.roleId !== null && this.roleId !== 'new' ? 'Roles.Edit' : 'Roles.Create');
  /** Branch path editing a role that already holds branch administration codes: the server refuses, so the page is read-only. */
  readonly readOnlyAdminRole = signal(false);

  readonly form = this.fb.nonNullable.group({
    name: ['', Validators.required],
    description: [''],
    // Scope is chosen on create and read-only on edit (the controls are disabled; the update request never carries them).
    scopeType: ['Company' as RoleScope],
    branchId: [''],
  });

  readonly scope = signal<RoleScope>('Company');
  readonly branches = signal<AccessibleBranch[]>([]);
  readonly branchLabel = signal<string | null>(null);

  readonly catalog = signal<PermissionCatalogEntry[]>([]);
  readonly selected = signal<Set<string>>(new Set());
  private initialSelected = new Set<string>();
  private saved = false;

  readonly saving = signal(false);
  readonly serverError = signal<string | null>(null);

  /** Branch roles may only pick branch-eligible codes. */
  readonly visibleCatalog = computed(() => {
    const eligible = eligibleCatalog(this.catalog(), this.scope());
    // FI-D45: branch administration codes are granted by the company path only.
    return this.branchOnly ? withoutBranchAdminCodes(eligible) : eligible;
  });

  readonly moduleSections = computed<ModuleSection[]>(() => {
    const byModule = new Map<string, Map<string, PermissionCatalogEntry[]>>();
    for (const entry of this.visibleCatalog()) {
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

  readonly totalCount = computed(() => this.visibleCatalog().length);
  readonly selectedCount = computed(() => this.selected().size);

  hasUnsavedChanges(): boolean {
    if (this.saved) return false;
    if (this.form.dirty) return true;
    return !setsEqual(this.selected(), this.initialSelected);
  }

  ngOnInit(): void {
    this.permissionsService.list().subscribe({ next: (list) => this.catalog.set(list) });

    if (!this.isEdit()) {
      if (this.branchOnly) {
        this.form.controls.scopeType.setValue('Branch');
        this.form.controls.scopeType.disable();
        this.form.controls.branchId.setValidators([Validators.required]);
        this.scope.set('Branch');
        // Locked to the working branch (no free choice); if there is no working branch the picker stays usable.
        const working = this.context.workingBranchId() ?? this.context.workingBranch()?.id ?? null;
        if (working) {
          this.form.controls.branchId.setValue(working);
          this.form.controls.branchId.disable();
          this.branchLabel.set(this.context.workingBranch()?.name ?? null);
        }
      }
      this.accessService.branches().subscribe({ next: (b) => this.branches.set(b), error: () => this.branches.set([]) });
      this.form.controls.scopeType.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((scope) => {
        this.scope.set(scope);
        this.form.controls.branchId.setValidators(scope === 'Branch' ? [Validators.required] : []);
        this.form.controls.branchId.updateValueAndValidity();
        this.selected.set(pruneSelection(this.selected(), this.catalog(), scope));
      });
    }

    if (this.isEdit()) {
      this.form.controls.scopeType.disable();
      this.form.controls.branchId.disable();
      this.rolesService.get(this.roleId!).subscribe({
        next: (role) => {
          this.scope.set(role.scopeType ?? 'Company');
          this.form.patchValue({ name: role.name, description: role.description ?? '', scopeType: role.scopeType ?? 'Company', branchId: role.branchId ?? '' });
          this.branchLabel.set(role.branchName);
          const set = new Set(role.permissionCodes);
          this.selected.set(set);
          this.initialSelected = new Set(set);
          this.readOnlyAdminRole.set(this.branchOnly && holdsBranchAdminCode(set));
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
    if (this.readOnlyAdminRole()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    const v = this.form.getRawValue();
    const codes = this.selected();
    const request$ = this.isEdit()
      ? this.rolesService.update(this.roleId!, buildUpdateRoleRequest(v, codes))
      : this.rolesService.create(buildCreateRoleRequest(v, codes));

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.saved = true;
        this.toast.show(this.isEdit() ? 'Role updated.' : 'Role created.');
        this.router.navigateByUrl('/administration/roles');
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        // Server field errors (name, scopeType, branchId, permissionCodes) land on the controls; the rest in the banner.
        this.serverError.set(applyProblem(this.form, toProblem(err)));
      },
    });
  }
}

function setsEqual(a: Set<string>, b: Set<string>): boolean {
  if (a.size !== b.size) return false;
  for (const item of a) if (!b.has(item)) return false;
  return true;
}
