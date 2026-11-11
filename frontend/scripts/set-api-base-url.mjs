// Phase 11 D9: set the production apiBaseUrl at build time from SELLORA_API_BASE_URL.
// Unset or empty: nothing changes (the committed relative '/api/v1' stays). Malformed: exit 1 so a typo cannot ship.
// Usage (from the repository root): SELLORA_API_BASE_URL=https://api.example.com node frontend/scripts/set-api-base-url.mjs
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const raw = (process.env.SELLORA_API_BASE_URL ?? '').trim();
const file = join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'environments', 'environment.ts');

if (raw === '') {
  console.log('SELLORA_API_BASE_URL not set: keeping the relative apiBaseUrl.');
  process.exit(0);
}
if (!/^https:\/\/[^/\s]+(\/[^\s]*)?$/.test(raw) || /['"`\\]/.test(raw)) {
  console.error(`SELLORA_API_BASE_URL must be an https URL without quotes, got: ${raw}`);
  process.exit(1);
}

const base = raw.replace(/\/+$/, '').replace(/\/api\/v1$/, '');
const value = `${base}/api/v1`;
const source = readFileSync(file, 'utf8');
const pattern = /apiBaseUrl:\s*'[^']*'/;
if (!pattern.test(source)) {
  console.error(`No apiBaseUrl literal found in ${file}.`);
  process.exit(1);
}
writeFileSync(file, source.replace(pattern, () => `apiBaseUrl: '${value}'`));
console.log(`apiBaseUrl set to ${value}`);
