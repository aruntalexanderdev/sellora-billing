import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal, type EnvironmentProviders, type Provider } from '@angular/core';
import { ContextService } from '../../core/context/context.service';

export interface ScopeStub {
  /** `CompanyWide` | `AssignedBranches` (default). */
  accessScope?: string;
  /** Number of allowed branches (default 1). */
  branches?: number;
}

/** Spec helper: a minimal ContextService + HTTP testing providers for components that render branch/location selects. */
export function provideScopeStubs(stub: ScopeStub = {}): (Provider | EnvironmentProviders)[] {
  const allowed = Array.from({ length: stub.branches ?? 1 }, (_, i) => ({ id: `b${i + 1}`, code: `B${i + 1}`, name: `Branch ${i + 1}`, isPrimary: i === 0, isDefault: i === 0 }));
  return [
    provideHttpClient(),
    provideHttpClientTesting(),
    {
      provide: ContextService,
      useValue: {
        accessScope: signal(stub.accessScope ?? 'AssignedBranches'),
        allowedBranches: signal(allowed),
        workingBranch: signal(allowed[0] ?? null),
        contextVersion: signal(0),
        companyPermissions: signal(new Set<string>()),
        companyViewActive: () => false,
        setCompanyView: () => undefined,
        company: signal({ id: 'c1', name: 'Acme' }),
      },
    },
  ];
}
