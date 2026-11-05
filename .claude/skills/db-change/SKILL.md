---
name: db-change
description: Safe schema change outside a restructuring phase — design, approval, migration, SQL review — following the ef-core-migrations skill.
argument-hint: <short description of the change>
disable-model-invocation: true
---
Schema change: $ARGUMENTS

Follow the `ef-core-migrations` skill (naming, additive → backfill → enforce → destructive, Id-preserving renames, validation SQL)
and `stock-ledger-invariants` if stock tables are touched.

1. Grep the related entities and `Data/Configurations`. If a feature spec exists, use its DB section.
2. Propose: columns + types, relationships/delete behaviour, indexes/constraints, permission codes, backfill, validation SQL,
   rollback. Flag anything destructive. STOP for approval.
3. After approval: implement (yourself for a single table; `database-engineer` for multi-table), add one migration.
4. Generate `dotnet ef migrations script <Previous> <Name> --idempotent ...` and summarise creates/alters/drops/indexes.
5. `scripts/verify.sh unit`. Never apply to a database — give the user the `database update` command to run themselves.
