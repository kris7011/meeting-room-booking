# Engineering standards and project conventions

These instructions apply to every code change, test, review, commit, and pull request in this repository.
Follow existing project conventions unless there is a documented, reviewable reason to improve them.

## Language and communication

- Use **English** for all source code, symbols, code comments, test names, error contracts, README files, technical documentation, branch names, commit messages, PR titles/descriptions, and review comments.
- Preserve any intentionally localized **user-facing UI** copy and translations. Do not translate UI strings without a product requirement.
- Write clear, technically precise commit messages, preferably Conventional Commits (e.g., `feat(api): ...`, `fix(bookings): ...`).

## Architecture and clean code

- Prefer readable, cohesive, small units with clear responsibilities; use names that express intent, not implementation trivia.
- Follow SOLID where it improves maintainability, especially single responsibility and dependency inversion at meaningful boundaries; favor composition over inheritance.
- Prefer the simplest architecture that meets current requirements. Do not introduce unnecessary patterns, generic abstractions, frameworks, microservices, layers, or dependencies.
- Preserve feature-oriented backend organization in `src/MeetingRoomBooking.Api/Features/` (endpoints, business services, domain models, and contracts by feature).
- Keep transport and persistence details outside domain rules where practical; apply dependency injection and explicit contracts at real boundaries.
- When a change requires architecture work, briefly explain trade-offs (maintainability, performance, testability, security, operability), and why the existing structure is insufficient.
- Avoid duplication, oversized classes, magic values, premature optimization, and speculative features. Refactor incrementally and avoid unrelated rewrites.

## C#, .NET, ASP.NET Core, EF Core, SQLite

- Target the versions pinned by the repository (currently .NET 10, EF Core 10). Check current official APIs before using unfamiliar or version-sensitive features.
- Use idiomatic modern C#: nullable reference types, correct cancellation propagation, `async`/`await` for I/O, dependency injection, and scoped lifetimes.
- Prefer ASP.NET Core Minimal API conventions already in use: thin endpoints, consistent Problem Details, clear HTTP status codes, validation, and authorization before business work.
- Use EF Core for application persistence and parameterized SQL for necessary raw commands. Avoid unnecessary `ToList`/client-side filtering, N+1 queries, unbounded database operations, and blocking database calls.
- Handle concurrency, transactions, idempotency, UTC/time zones, and SQLite's limitations explicitly when relevant. Preserve overlap and last-Owner database invariants.

## TypeScript / Angular web application

- Follow the existing Angular 21 project structure and its actual npm scripts.
- Use strict typing; avoid `any` and unsafe assertions unless justified.
- Prefer Angular's current recommended patterns (standalone components, signals, typed reactive forms) where consistent with the existing code.
- Keep UI components focused; isolate API calls, state transitions, validation, and domain rules at appropriate boundaries.
- Never bypass authentication or authorization merely to make the old web UI work.

## Security and data safety

- Never trust client-supplied user identities, organization IDs, or roles as authorization proof. Validate JWTs server-side and enforce tenant membership and role permissions on **every** affected endpoint.
- Ensure every organization-scoped query and mutation is constrained by the authorized organization; prevent cross-tenant information leakage.
- Never commit secrets, tokens, real databases, private credentials, `.env.local`, or unnecessary personal data. Do not log access or refresh tokens.
- Treat migration, export, import, restore, and cleanup as data-loss risks. Take verified backups before schema changes. Do not overwrite or silently reset a database.
- Use HTTPS for remote authenticated requests. Keep the legacy anonymous demo mode disabled outside its explicitly allowed development/testing use.

## Tests, review, and completion

- For behavior changes, add or update focused tests, covering successful flows, invalid inputs, boundary conditions, authorization failures, and concurrency where relevant.
- Use xUnit and integration tests with isolated temporary SQLite databases; do not point tests at the developer's real database.
- Before considering backend work complete, run `dotnet build ./MeetingRoomBooking.sln --configuration Release` and `dotnet test ./MeetingRoomBooking.sln --configuration Release`. If the portability tool was changed, build `./tools/RoomBooking.DatabaseTool/RoomBooking.DatabaseTool.csproj` as well.
- For Angular changes, run the relevant `npm run build` and test commands from `src/meeting-room-booking-web` as defined in its `package.json`.
- Review the diff for correctness, readability, accidental scope changes, security vulnerabilities, missing tests, and documentation drift.
- Prefer small, focused PRs with an English summary, rationale, risk/rollback notes for database work, and explicit test evidence. Never claim a check passed unless it actually ran.
- Leave unrelated changes untouched; do not force-reset, discard local work, merge, or deploy without authorization.

## Technical understanding (part of the Definition of Done)

The repository owner must be able to explain, demonstrate, troubleshoot, and defend every important part of this system.

- When implementing or significantly changing a feature, give the owner a walkthrough: the problem solved, how it works, the files/classes/methods involved, the request and data flow, why this design was chosen over alternatives, how authentication, authorization, validation, and security are handled, how it is tested, and its failure modes and limitations. Reference real code; never invent details or claim untested behavior is tested.
- Keep [`docs/PROJECT_WALKTHROUGH.md`](docs/PROJECT_WALKTHROUGH.md) accurate in the same PR as the change, using its status labels (Tested, Manual, In review, Not implemented).
- Record significant architectural decisions as short ADRs in [`docs/adr/`](docs/adr/README.md). Avoid documentation that nobody needs.
- Be transparent about AI assistance: keep `Co-Authored-By` trailers on AI-assisted commits, and never present AI-generated code as hand-written.

## Operations and portability

- The user must be able to run the **same .NET API and recover SQLite data locally**, even if paid hosting is cancelled.
- Keep hosting-specific configuration outside business logic; prefer environment-based configuration over provider lock-in.
- Document local startup, verified backup, restore to a **new** database, and cloud-exit steps as functionality evolves.
