import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import type { DbReleasesOverviewDto, DbScriptDetailDto, DbScriptDto, DbReleaseRunDto } from './platform.models';
import { PlatformDbReleases, formatDuration, scriptMatchesFilter } from './platform-db-releases';
import { PlatformService } from './platform.service';

const tick = () => new Promise<void>((r) => setTimeout(r, 0));

const script = (o: Partial<DbScriptDto> & Pick<DbScriptDto, 'id' | 'name' | 'status'>): DbScriptDto => ({
  kind: 'Data', attemptCount: 1, ...o,
});
const run = (o: Partial<DbReleaseRunDto> = {}): DbReleaseRunDto => ({
  id: 'r1', environment: 'claude-dev', commitSha: 'abcdef1234567890', appVersion: '1.4.0', trigger: 'Deploy', triggeredBy: 'ci',
  status: 'Failed', startedAt: '2026-11-18T10:00:00Z', completedAt: '2026-11-18T10:00:05Z', pipelineUrl: 'https://github.com/x/actions/runs/1', ...o,
});

const overview = (): DbReleasesOverviewDto => ({
  journalAvailable: true,
  runs: [run(), run({ id: 'r0', status: 'Succeeded', pipelineUrl: null })],
  scripts: [
    script({ id: 'e1', name: '20260101_Init', kind: 'EfMigration', status: 'Succeeded' }),
    script({ id: 'd1', name: '2026.11.01.001__a.sql', status: 'Succeeded' }),
    script({ id: 'd2', name: '2026.11.02.001__b.sql', status: 'Failed', lastSqlState: '23502', lastError: 'null value' }),
    script({ id: 'd3', name: '2026.11.03.001__c.sql', status: 'Pending', attemptCount: 0 }),
    script({ id: 'v1', name: 'V__x.sql', kind: 'Validation', status: 'Warning' }),
    script({ id: 'd4', name: '2026.10.01.001__old.sql', status: 'Removed' }),
  ],
});

const detail: DbScriptDetailDto = {
  script: overview().scripts[2],
  sql: 'UPDATE "Products" SET x = 1;',
  attempts: [{ id: 'a1', scriptId: 'd2', scriptName: '2026.11.02.001__b.sql', scriptKind: 'Data', releaseRunId: 'r1', status: 'Failed',
    startedAt: '2026-11-18T10:00:01Z', durationMs: 420, rowsAffected: null, sqlState: '23502', errorMessage: 'null value', checksum: 'abcdef0123456789ffff' }],
};

function mount(data: DbReleasesOverviewDto | 'error', extra: Record<string, unknown> = {}) {
  const service = {
    dbReleases: vi.fn(() => (data === 'error' ? throwError(() => new HttpErrorResponse({ status: 500 })) : of(data))),
    dbScript: vi.fn(() => of(detail)),
    dbReleaseRun: vi.fn(() => of({ run: run(), attempts: detail.attempts })),
    ...extra,
  };
  TestBed.configureTestingModule({ imports: [PlatformDbReleases], providers: [{ provide: PlatformService, useValue: service }] });
  const fixture = TestBed.createComponent(PlatformDbReleases);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const names = () => Array.from(el.querySelectorAll('tr.click .name')).map((n) => n.textContent?.trim());
  const text = (s: string) => Array.from(el.querySelectorAll<HTMLElement>(s)).find(Boolean);
  return { fixture, el, service, names, text };
}

describe('PlatformDbReleases', () => {
  it('renders summary tiles, latest run and rows (EF collapsed, removed muted)', () => {
    const { el, names } = mount(overview());
    const tiles = Array.from(el.querySelectorAll('.tile')).map((t) => t.textContent?.trim());
    expect(tiles).toEqual(['6All', '2Succeeded', '1Failed', '1Pending', '1Warnings']);
    expect(el.textContent).toContain('claude-dev');
    expect(el.textContent).toContain('abcdef1');
    expect(el.textContent).not.toContain('abcdef12');
    expect(names()).not.toContain('20260101_Init');
    expect(names()).toHaveLength(5);
    expect(el.querySelector('tr.muted .name')?.textContent).toContain('old.sql');
    const link = el.querySelector<HTMLAnchorElement>('a[target="_blank"]')!;
    expect(link.getAttribute('href')).toBe('https://github.com/x/actions/runs/1');
    expect(link.getAttribute('rel')).toBe('noopener');
  });

  it('filters by tile', () => {
    const { el, fixture, names } = mount(overview());
    el.querySelectorAll<HTMLButtonElement>('.tile')[2].click();
    fixture.detectChanges();
    expect(names()).toEqual(['2026.11.02.001__b.sql']);
    el.querySelectorAll<HTMLButtonElement>('.tile')[1].click();
    fixture.detectChanges();
    expect(names()).toEqual(['2026.11.01.001__a.sql']);
  });

  it('toggles EF migrations', () => {
    const { el, fixture, names } = mount(overview());
    const toggle = el.querySelector<HTMLButtonElement>('.ef-toggle button')!;
    expect(toggle.textContent?.trim()).toBe('Show 1 EF migrations');
    toggle.click();
    fixture.detectChanges();
    expect(names()).toContain('20260101_Init');
  });

  it('shows the retry hint for a failed script and failed run', async () => {
    const { el, fixture } = mount(overview());
    expect(el.textContent).toContain('To retry, fix the script in a PR (or re-run the failed migrate-db job in GitHub)');
    el.querySelectorAll<HTMLElement>('tr.click')[1].click(); // Failed row
    fixture.detectChanges();
    await tick();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="run-failed-hint"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="script-failed-hint"]')).toBeTruthy();
    expect(el.textContent).not.toContain('Retry from this script');
  });

  it('shows the designed empty state when the journal is unavailable', () => {
    const { el } = mount({ journalAvailable: false, runs: [], scripts: [] });
    expect(el.querySelector('[data-testid="journal-empty"]')?.textContent).toContain('The migrator has not run on this database yet.');
    expect(el.querySelector('.tiles')).toBeNull();
  });

  it('shows an error state with retry on load failure', () => {
    const { el } = mount('error');
    expect(el.querySelector('[role="alert"]')).toBeTruthy();
  });

  it('opens the detail panel and loads script detail', async () => {
    const { el, fixture, service } = mount(overview());
    el.querySelectorAll<HTMLElement>('tr.click')[1].click();
    fixture.detectChanges();
    await tick();
    fixture.detectChanges();
    expect(service.dbScript).toHaveBeenCalledWith('d2');
    const panel = el.querySelector('[data-testid="script-panel"]')!;
    expect(panel.querySelector('pre.sql')?.textContent).toContain('UPDATE "Products"');
    expect(panel.textContent).toContain('abcdef012345');
    expect(panel.textContent).not.toContain('abcdef0123456');
    expect(panel.textContent).toContain('23502');
  });

  it('omits the sql block when sql is null', async () => {
    const { el, fixture } = mount(overview(), { dbScript: vi.fn(() => of({ ...detail, sql: null })) });
    el.querySelectorAll<HTMLElement>('tr.click')[1].click();
    fixture.detectChanges();
    await tick();
    fixture.detectChanges();
    expect(el.querySelector('pre.sql')).toBeNull();
  });

  it('loads a run detail from recent runs', async () => {
    const { el, fixture, service } = mount(overview());
    el.querySelectorAll<HTMLElement>('.run')[1].click();
    fixture.detectChanges();
    await tick();
    fixture.detectChanges();
    expect(service.dbReleaseRun).toHaveBeenCalledWith('r0');
    expect(el.querySelector('[data-testid="run-panel"]')?.textContent).toContain('2026.11.02.001__b.sql');
  });
});

describe('db release helpers', () => {
  it('maps filters', () => {
    expect(scriptMatchesFilter('Passed', 'succeeded')).toBe(true);
    expect(scriptMatchesFilter('Baselined', 'succeeded')).toBe(false);
    expect(scriptMatchesFilter('Warning', 'warning')).toBe(true);
  });
  it('formats durations', () => {
    expect(formatDuration(null)).toBe('—');
    expect(formatDuration(420)).toBe('420 ms');
    expect(formatDuration(1500)).toBe('1.5 s');
    expect(formatDuration(125000)).toBe('2 min 5 s');
  });
});
