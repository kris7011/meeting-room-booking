# Architecture Decision Records

Short records of decisions that shape the system. Each states the context,
the decision, the alternatives and the consequences.

ADRs 0001 and 0002 were **recorded retroactively** on 2026-10-24: the
decisions were made earlier, and their rationale is reconstructed from the
code, code comments and README, not from notes written at the time.

| ADR | Decision | Status |
| --- | --- | --- |
| [0001](0001-organisation-scoped-access-control.md) | Organisation-scoped access control in one endpoint filter | Accepted |
| [0002](0002-database-enforced-invariants.md) | Critical invariants enforced by the database as well as the services | Accepted |
| [0003](0003-portable-sqlite-backup-and-restore.md) | Portable operation with SQLite snapshots and restore to a new file | Accepted (PR #13 merged) |
| [0004](0004-reauthorize-mutations-under-the-write-lock.md) | Reauthorize every organisation change under the write lock | Accepted (PR #16 merged) |
| [0005](0005-idempotent-booking-creation.md) | Idempotent booking creation with a persisted Idempotency-Key | Accepted (PR #18 merged) |
| [0006](0006-booking-details-hidden-from-members.md) | Booking titles and bookers hidden from Members | Accepted (PR #19 merged) |
| [0007](0007-browser-sign-in-for-the-angular-client.md) | Browser sign-in for the Angular client (MSAL, redirect, own-origin token) | Accepted |

New ADRs: copy the structure of an existing one, number them sequentially,
and keep them to one page.
