-- sellora:description Tax profiles TP-5a (M3a): set TaxProfileId on historic sales/purchase lines from their legacy TaxId (1:1 mirror mapping), then on return lines from their original line. The version stays NULL.
-- sellora:transaction none
-- sellora:timeout 14400
-- docs/features/tax-profiles.md, "TP-5 design", "History backfill (M3a, additive)". Runs AFTER the EF migrations PrepareTaxHistoryBackfill and ValidateTaxHistoryBackfill (name order, same release).
--
-- WHY transaction: none. The work is committed every batch (default 5,000 rows) so no single transaction holds locks on, or bloats, a production-sized line table, and sales/purchases continue
-- while it runs. The whole script is ONE statement (a DO block; COMMIT inside it is legal because it is the top-level statement outside a transaction block), as the migrator requires.
--
-- Safe by construction:
--   * Idempotent and resumable: only rows with "TaxProfileId" IS NULL are touched. Kill it (cancel, statement_timeout, deploy abort) and run it again: finished batches stay committed, the rest is picked up.
--   * Never blocks writers: rows are taken FOR UPDATE SKIP LOCKED. A row that is being edited right now is skipped and picked up by the next run (the final NOTICE shows how many remain).
--   * Keyset paging on the primary key ("Id" > last processed), so each batch is an index range scan; the cost is linear in the table size, not quadratic.
--   * History keeps NO version (BR24: a snapshot is never invented). Lines whose TaxId has no mirror profile in the document's company stay NULL (V__tax-history-profile.sql reports them).
--   * Order: SalesInvoiceLines and PurchaseOrderLines (profile = TaxProfiles where CompanyId = header company and LegacyTaxId = line TaxId), THEN SalesReturnLines and PurchaseReturnLines (copy the
--     original line's profile, only where the return line has none and the original has one). Lines saved by the TP-3/TP-4 build already have a profile and are never changed.
--
-- Batch size: 5,000. Operators and tests may lower it for the session:  PGOPTIONS="-c sellora.backfill_batch_size=500" psql ...   (a custom setting, read below; no effect on the released default).
DO $backfill$
DECLARE
  v_batch   integer := COALESCE(NULLIF(current_setting('sellora.backfill_batch_size', true), '')::integer, 5000);
  v_tables  text[] := ARRAY['SalesInvoiceLines', 'PurchaseOrderLines', 'SalesReturnLines', 'PurchaseReturnLines'];
  -- Per table: the rows of the key window (Id > $1 AND Id <= $2) that still have no profile and the profile they get, locked SKIP LOCKED.
  v_sources text[] := ARRAY[
    'SELECT l."Id", p."Id" AS "ProfileId" FROM "SalesInvoiceLines" l JOIN "SalesInvoices" h ON h."Id" = l."SalesInvoiceId" '
      'JOIN "TaxProfiles" p ON p."CompanyId" = h."CompanyId" AND p."LegacyTaxId" = l."TaxId" '
      'WHERE l."Id" > $1 AND l."Id" <= $2 AND l."TaxProfileId" IS NULL FOR UPDATE OF l SKIP LOCKED',
    'SELECT l."Id", p."Id" AS "ProfileId" FROM "PurchaseOrderLines" l JOIN "PurchaseOrders" h ON h."Id" = l."PurchaseOrderId" '
      'JOIN "TaxProfiles" p ON p."CompanyId" = h."CompanyId" AND p."LegacyTaxId" = l."TaxId" '
      'WHERE l."Id" > $1 AND l."Id" <= $2 AND l."TaxProfileId" IS NULL FOR UPDATE OF l SKIP LOCKED',
    'SELECT l."Id", o."TaxProfileId" AS "ProfileId" FROM "SalesReturnLines" l JOIN "SalesInvoiceLines" o ON o."Id" = l."SalesInvoiceLineId" '
      'WHERE l."Id" > $1 AND l."Id" <= $2 AND l."TaxProfileId" IS NULL AND o."TaxProfileId" IS NOT NULL FOR UPDATE OF l SKIP LOCKED',
    'SELECT l."Id", o."TaxProfileId" AS "ProfileId" FROM "PurchaseReturnLines" l JOIN "PurchaseOrderLines" o ON o."Id" = l."PurchaseOrderLineId" '
      'WHERE l."Id" > $1 AND l."Id" <= $2 AND l."TaxProfileId" IS NULL AND o."TaxProfileId" IS NOT NULL FOR UPDATE OF l SKIP LOCKED'
  ];
  v_last    uuid;
  v_hi      uuid;
  v_rows    bigint;
  v_total   bigint;
  v_batches integer;
  v_left    bigint;
  i         integer;
BEGIN
  IF v_batch < 1 THEN
    RAISE EXCEPTION 'sellora.backfill_batch_size must be at least 1, got %', v_batch;
  END IF;
  RAISE NOTICE 'tax-history-profile-backfill: batch size %', v_batch;

  FOR i IN 1 .. array_length(v_tables, 1) LOOP
    v_last := '00000000-0000-0000-0000-000000000000';
    v_total := 0;
    v_batches := 0;
    LOOP
      -- upper bound of the next key window of v_batch rows (no lock, index scan)
      EXECUTE format('SELECT w."Id" FROM (SELECT "Id" FROM %I WHERE "Id" > $1 ORDER BY "Id" LIMIT $2) w ORDER BY w."Id" DESC LIMIT 1', v_tables[i])
        INTO v_hi USING v_last, v_batch;
      EXIT WHEN v_hi IS NULL;

      EXECUTE format('UPDATE %I t SET "TaxProfileId" = s."ProfileId" FROM (%s) s WHERE t."Id" = s."Id" AND t."TaxProfileId" IS NULL', v_tables[i], v_sources[i])
        USING v_last, v_hi;
      GET DIAGNOSTICS v_rows = ROW_COUNT;
      COMMIT;

      v_total := v_total + v_rows;
      v_batches := v_batches + 1;
      v_last := v_hi;
      IF v_rows > 0 THEN
        RAISE NOTICE '% batch %: % rows (% so far), key window up to %', v_tables[i], v_batches, v_rows, v_total, v_last;
      END IF;
    END LOOP;

    -- rows still without a profile: no mirror profile / original without a profile (legitimately NULL), or skipped because they were locked: run the script again to find out
    EXECUTE format('SELECT count(*) FROM %I WHERE "TaxProfileId" IS NULL', v_tables[i]) INTO v_left;
    RAISE NOTICE '% done: % rows set in % windows; % rows still without a profile', v_tables[i], v_total, v_batches, v_left;
  END LOOP;
END
$backfill$;
