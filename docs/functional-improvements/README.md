# Sellora Billing — Approved Implementation Package

Baseline: Functional Improvements Review v1.4, approved 2026-11-12.

## Contents
- `IMPLEMENTATION_SPEC.md`: full functional, architectural, security and data-integrity requirements.
- `PHASE_PLAN.md`: nine sequenced phases, tasks, acceptance criteria and approval gates.
- `prompts/PHASE_*.md`: standalone Claude Code prompts, one per phase.
- `CLAUDE_REFERENCE.md`: minimal instructions for linking these documents from an existing CLAUDE.md.

## Usage
Copy this folder under `docs/functional-improvements/` in the Sellora repository (or adapt references to your chosen path). Add the reference in `CLAUDE_REFERENCE.md` to the existing root `CLAUDE.md`. Start with Phase 01 prompt. Claude should audit and plan first, wait for approval, implement only the approved phase, test, report, then stop. Do not paste all prompts at once.

**Caution:** This package is based on approved user requirements and known architecture context, not a fresh audit of repository source. Claude must verify existing implementation before edits.
