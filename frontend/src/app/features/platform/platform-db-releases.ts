import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { problemMessage, toProblem } from '../../core/http/api-errors';
import { UiBadge, type BadgeTone } from '../../shared/ui/badge/badge';
import { UiButton } from '../../shared/ui/button/button';
import type {
  DbReleaseRunDetailDto, DbReleaseRunDto, DbReleaseRunStatus, DbReleasesOverviewDto, DbScriptDetailDto, DbScriptDto, DbScriptStatus,
} from './platform.models';
import { PlatformService } from './platform.service';

export type DbScriptFilter = 'all' | 'succeeded' | 'failed' | 'pending' | 'warning';

export const RETRY_HINT = 'To retry, fix the script in a PR (or re-run the failed migrate-db job in GitHub)';

const FILTERS: { key: DbScriptFilter; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'succeeded', label: 'Succeeded' },
  { key: 'failed', label: 'Failed' },
  { key: 'pending', label: 'Pending' },
  { key: 'warning', label: 'Warnings' },
];

export function scriptMatchesFilter(status: DbScriptStatus, filter: DbScriptFilter): boolean {
  switch (filter) {
    case 'all': return true;
    case 'succeeded': return status === 'Succeeded' || status === 'Passed';
    case 'failed': return status === 'Failed';
    case 'pending': return status === 'Pending';
    case 'warning': return status === 'Warning';
  }
}

export function scriptTone(status: string): BadgeTone {
  switch (status) {
    case 'Succeeded': case 'Passed': return 'good';
    case 'Failed': return 'bad';
    case 'Warning': return 'warn';
    case 'Running': return 'info';
    default: return 'mute'; // Pending, Baselined, Removed
  }
}

export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return '—';
  if (ms < 1000) return `${ms} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(1)} s`;
  return `${Math.floor(s / 60)} min ${Math.round(s % 60)} s`;
}

export function runDurationMs(run: DbReleaseRunDto): number | null {
  if (!run.completedAt) return null;
  const d = new Date(run.completedAt).getTime() - new Date(run.startedAt).getTime();
  return Number.isFinite(d) && d >= 0 ? d : null;
}

/** Platform admin view of the migrator journal (db-release-automation D8: view only, no retry). */
@Component({
  selector: 'app-platform-db-releases',
  standalone: true,
  imports: [DatePipe, UiBadge, UiButton],
  templateUrl: './platform-db-releases.html',
  styleUrl: './platform-db-releases.scss',
})
export class PlatformDbReleases implements OnInit {
  private readonly service = inject(PlatformService);

  readonly filters = FILTERS;
  readonly retryHint = RETRY_HINT;
  readonly tone = scriptTone;
  readonly duration = formatDuration;
  readonly runMs = runDurationMs;

  readonly state = signal<'loading' | 'error' | 'ready'>('loading');
  readonly errorMessage = signal('');
  readonly overview = signal<DbReleasesOverviewDto | null>(null);
  readonly filter = signal<DbScriptFilter>('all');
  readonly showEf = signal(false);

  readonly selectedScriptId = signal<string | null>(null);
  readonly scriptDetail = signal<DbScriptDetailDto | null>(null);
  readonly scriptState = signal<'idle' | 'loading' | 'error'>('idle');
  readonly scriptError = signal('');

  readonly selectedRunId = signal<string | null>(null);
  readonly runDetail = signal<DbReleaseRunDetailDto | null>(null);
  readonly runState = signal<'idle' | 'loading' | 'error'>('idle');
  readonly runError = signal('');

  readonly scripts = computed(() => this.overview()?.scripts ?? []);
  readonly runs = computed(() => this.overview()?.runs ?? []);
  readonly latest = computed<DbReleaseRunDto | null>(() => this.runs()[0] ?? null);

  readonly counts = computed<Record<DbScriptFilter, number>>(() => {
    const all = this.scripts();
    const n = (f: DbScriptFilter) => all.filter((s) => scriptMatchesFilter(s.status, f)).length;
    return { all: all.length, succeeded: n('succeeded'), failed: n('failed'), pending: n('pending'), warning: n('warning') };
  });

  private readonly filtered = computed(() => this.scripts().filter((s) => scriptMatchesFilter(s.status, this.filter())));
  readonly efCount = computed(() => this.filtered().filter((s) => s.kind === 'EfMigration').length);
  readonly visible = computed(() => (this.showEf() ? this.filtered() : this.filtered().filter((s) => s.kind !== 'EfMigration')));

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.state.set('loading');
    this.service.dbReleases().subscribe({
      next: (o) => {
        this.overview.set(o);
        this.state.set('ready');
      },
      error: (err: HttpErrorResponse) => {
        this.errorMessage.set(problemMessage(toProblem(err), 'Could not load database releases.'));
        this.state.set('error');
      },
    });
  }

  setFilter(f: DbScriptFilter): void {
    this.filter.set(f);
  }

  toggleEf(): void {
    this.showEf.update((v) => !v);
  }

  isMuted(s: DbScriptDto): boolean {
    return s.status === 'Removed' || s.status === 'Baselined';
  }

  runTone(status: DbReleaseRunStatus): BadgeTone {
    return scriptTone(status);
  }

  shortSha(sha: string | null | undefined): string {
    return sha ? sha.slice(0, 7) : '';
  }

  shortChecksum(checksum: string | null | undefined): string {
    return checksum ? checksum.slice(0, 12) : '';
  }

  openScript(s: DbScriptDto): void {
    this.selectedScriptId.set(s.id);
    this.scriptDetail.set(null);
    this.scriptState.set('loading');
    this.service.dbScript(s.id).subscribe({
      next: (d) => {
        if (this.selectedScriptId() !== s.id) return;
        this.scriptDetail.set(d);
        this.scriptState.set('idle');
      },
      error: (err: HttpErrorResponse) => {
        if (this.selectedScriptId() !== s.id) return;
        this.scriptError.set(problemMessage(toProblem(err), 'Could not load this script.'));
        this.scriptState.set('error');
      },
    });
  }

  closeScript(): void {
    this.selectedScriptId.set(null);
    this.scriptDetail.set(null);
    this.scriptState.set('idle');
  }

  openRun(r: DbReleaseRunDto): void {
    this.selectedRunId.set(r.id);
    this.runDetail.set(null);
    this.runState.set('loading');
    this.service.dbReleaseRun(r.id).subscribe({
      next: (d) => {
        if (this.selectedRunId() !== r.id) return;
        this.runDetail.set(d);
        this.runState.set('idle');
      },
      error: (err: HttpErrorResponse) => {
        if (this.selectedRunId() !== r.id) return;
        this.runError.set(problemMessage(toProblem(err), 'Could not load this run.'));
        this.runState.set('error');
      },
    });
  }

  closeRun(): void {
    this.selectedRunId.set(null);
    this.runDetail.set(null);
    this.runState.set('idle');
  }
}
