import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { applyProblem, problemMessage, toProblem } from '../../core/http/api-errors';
import { UiBadge, type BadgeTone } from '../../shared/ui/badge/badge';
import { UiButton } from '../../shared/ui/button/button';
import type {
  DbReleaseRunDetailDto, DbReleaseRunDto, DbReleaseRunStatus, DbReleasesOverviewDto, DbScriptDetailDto, DbScriptDto, DbScriptStatus,
} from './platform.models';
import { PlatformService } from './platform.service';

export type DbScriptFilter = 'all' | 'succeeded' | 'failed' | 'pending' | 'warning';

export const RETRY_HINT = 'Script bug: fix it in a PR and deploy again (a retry re-applies the same release). Temporary error (lock, connection): retry.';

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

export const REASON_MIN = 10;
export const REASON_MAX = 500;

function reasonLength(c: AbstractControl): ValidationErrors | null {
  const n = String(c.value ?? '').trim().length;
  if (n < REASON_MIN) return { minlength: true };
  if (n > REASON_MAX) return { maxlength: true };
  return null;
}

export interface RetryNotice { kind: 'ok' | 'error'; text: string; url?: string }

/** Platform admin view of the migrator journal, with retry of the latest failed run (db-release-automation D20-D23). */
@Component({
  selector: 'app-platform-db-releases',
  standalone: true,
  imports: [DatePipe, ReactiveFormsModule, UiBadge, UiButton],
  templateUrl: './platform-db-releases.html',
  styleUrl: './platform-db-releases.scss',
})
export class PlatformDbReleases implements OnInit {
  private readonly service = inject(PlatformService);
  private readonly fb = inject(FormBuilder);

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

  readonly reasonMax = REASON_MAX;
  readonly reasonMin = REASON_MIN;
  readonly retryForm = this.fb.nonNullable.group({ reason: ['', [Validators.required, reasonLength]] });
  readonly retryOpen = signal(false);
  readonly retrySubmitting = signal(false);
  readonly retryError = signal('');
  readonly retryNotice = signal<RetryNotice | null>(null);
  private readonly retryPanel = viewChild<ElementRef<HTMLElement>>('retryPanel');

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

  load(silent = false): void {
    if (!silent) this.state.set('loading');
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

  get reasonLen(): number {
    return this.retryForm.controls.reason.value.length;
  }

  reasonError(): string {
    const c = this.retryForm.controls.reason;
    if (!c.touched && !c.errors?.['server']) return '';
    if (c.errors?.['server']) return String(c.errors['server']);
    if (c.errors?.['required'] || c.errors?.['minlength']) return `Enter at least ${REASON_MIN} characters.`;
    if (c.errors?.['maxlength']) return `Use at most ${REASON_MAX} characters.`;
    return '';
  }

  openRetry(): void {
    this.retryOpen.set(true);
    this.retryError.set('');
    this.retryNotice.set(null);
    setTimeout(() => {
      const el = this.retryPanel()?.nativeElement;
      el?.scrollIntoView?.({ block: 'center', behavior: 'smooth' });
      el?.querySelector('textarea')?.focus();
    });
  }

  cancelRetry(): void {
    this.retryOpen.set(false);
    this.retryError.set('');
    this.retryForm.reset();
  }

  startRetry(): void {
    const run = this.latest();
    this.retryForm.markAllAsTouched();
    if (!run || this.retryForm.invalid || this.retrySubmitting()) return;
    this.retrySubmitting.set(true);
    this.retryError.set('');
    this.service.retryDbRelease({ environment: run.environment, reason: this.retryForm.controls.reason.value.trim() }).subscribe({
      next: (r) => {
        this.retrySubmitting.set(false);
        this.cancelRetry();
        this.retryNotice.set({ kind: 'ok', text: 'Retry requested. GitHub is re-running the failed jobs.', url: r.pipelineUrl });
        this.load(true);
      },
      error: (err: HttpErrorResponse) => {
        this.retrySubmitting.set(false);
        const problem = toProblem(err);
        const status = err.status;
        if (status === 400) {
          this.retryError.set(applyProblem(this.retryForm, problem) ?? '');
        } else if (status === 502) {
          this.retryError.set(problem?.title || 'GitHub rejected the retry request. Try again.');
        } else if (status === 409 || status === 503) {
          this.cancelRetry();
          this.retryNotice.set({ kind: 'error', text: problem?.title || problemMessage(problem) });
          this.load(true);
        } else {
          this.retryError.set(problemMessage(problem, 'Could not start the retry.'));
        }
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
