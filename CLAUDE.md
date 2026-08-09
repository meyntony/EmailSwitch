# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build EmailSwitch.sln --configuration Release -warnaserror   # must stay at 0 warnings
dotnet test EmailSwitch.sln --configuration Release
dotnet pack --configuration Release --no-build --output .           # only EmailSwitch is packable
```

Run one test class or one test:

```bash
dotnet test EmailSwitch.sln --filter "FullyQualifiedName~SessionExpiryTests"
dotnet test EmailSwitch.sln --filter "FullyQualifiedName~SessionExpiryTests.A_fresh_session_with_no_queue_yet_has_not_expired"
```

**This repository runs on VSTest, so `--filter` is correct here.** The sibling
MongoDbTokenManager runs on Microsoft.Testing.Platform and needs `--filter-class` instead; using the
wrong one runs zero tests and exits 5 rather than erroring usefully.

Part of the suite needs a reachable MongoDB (`mongodb://localhost:27017`, override with
`MONGODB_CONNECTION_STRING`). `.github/workflows/ci.yml` provides a `mongo:8` service for this;
`release.yml` runs no tests and needs none.

Under `-warnaserror` the xUnit analyzers are errors too. The one that bites: **xUnit1051** fails any
call accepting a `CancellationToken` made *directly inside a test method* without passing
`TestContext.Current.CancellationToken`. Moving the call into a private helper is usually the tidier
fix. Where cleanup must run even on cancellation — dropping a test database — the existing code
passes no token on purpose and suppresses the rule locally; keep that.

## What this is

A NuGet library that sends and verifies email one-time passcodes. It owns the session, the code, its
expiry, the attempt limits and the audit trail. It is on the authentication path, so concurrency and
correctness matter more than the line count suggests.

Three layers: `EmailSwitchService` (the switchboard) → provider services implementing
`IServiceEmails` (SendGrid, DevConsole) → the provider SDK. Providers are resolved by keyed DI on the
`EmailProvider` enum, so adding one is a registration plus an implementation, not a new `switch` arm.

**The provider contract is deliberately tiny**: `IServiceEmails.SendOTP(EmailIdentifier, EmailContent)`.
`EmailSwitchDbService.GetOrCreateAndGetLatestSession` mints the code through MongoDbTokenManager and
renders the whole email *before* any provider is involved. Consequences worth internalising:

- Providers never touch tokens, so a new provider is close to trivial (`DevConsoleService` is ~60 lines).
- `VerifyOTP` never routes to a provider — verification goes through the session and the token.
- This is the main structural difference from SMSwitch, where the provider owns the whole OTP
  lifecycle. Do not port SMSwitch's provider design across.

## The provider queue is a send budget, not session state

This diverges from SMSwitch **on purpose**, and the divergence is the fix for a real lockout bug.

- Built once by `EmailSwitchService.BuildProviderQueue` from `Priority` repeated
  `MaxRoundRobinAttempts` times. `Priority.Count × MaxRoundRobinAttempts` is the total emails one
  session may ever send.
- **Null means not started; empty means spent.** Only a null queue is rebuilt. Refilling an empty one
  gives unlimited resends.
- **Every attempt spends a slot, success included.** Only a failure falls through to the next
  provider.
- **An empty queue does not kill the session.** `HasNotExpired` says nothing about the queue.

SMSwitch does the opposite on both of the last two points — it leaves the successful provider at the
head (its `VerifyOTP` peeks it to route verification back) and treats an empty queue as expiry. Both
choices are wrong here: nothing routes by provider, and conflating "out of sends" with "expired" meant
one resend click made a delivered, in-date code stop verifying. Do not "restore" either behaviour.

## Persistence constraints

Non-obvious, and each of these has caused a real bug:

- **Session timestamps are UTC `DateTime`, not `DateTimeOffset`.** The driver stores a
  `DateTimeOffset` as a subdocument, which cannot be indexed or range-queried as an instant. Storing a
  plain BSON date is what lets `GetLatestSession` push the expiry filter and the sort to the server.
  SMSwitch still uses `DateTimeOffset` and relies on subfield ordering — do not copy that here.
  `SessionSerializationTests` pins this, including a contrast test showing what `DateTimeOffset` would
  serialise to.
- **`EmailProvider` values are persisted by number** inside `EmailProvidersQueue` and are pinned
  explicitly. Append new members; renumbering reinterprets every stored session.
- **Mutate server-side, never read-modify-write.** A read-mutate-replace cycle loses increments under
  concurrency — measured at 25 concurrent failures recorded as 2. Every write to a session is a
  targeted `UpdateOne`: `RegisterFailedVerificationAttempt`, `RegisterSuccessfulVerification`,
  `RegisterRenderRequest`, `RegisterSendAttempts`. There is deliberately **no whole-document
  replace**. `SendOTP` used to have one, and because it read the session before a provider call and
  wrote it back after, it reverted anything recorded in that window — including the brute-force
  counter. Do not reintroduce an `UpdateSession(session)`.
- **The attempt cap is claimed before the guess is checked, not counted after.**
  `TryReserveVerificationAttempt` is a `findAndModify` that tests the limit and `$inc`s
  `VerificationAttemptsCount` in one operation; a refused claim never reaches `ConsumeAndValidate`.
  Reading the session, testing `HasNotExpired` and counting afterwards is check-then-act — measured
  at **16 concurrent guesses admitted against a cap of 3** on a local single-node MongoDB, and the
  window scales with provider latency. `HasNotExpired` is advisory only; it describes the session as
  loaded and cannot enforce anything. This is the **only** guard on a six digit code since
  MongoDbTokenManager 10.2.0 deleted its own `MAXIMUM_ATTEMPTS = 5`.
- **`VerificationAttemptsCount` and `FailedVerificationAttemptsUTC` are not the same thing.** The
  first is the rate-limit reservation, claimed by every guess including a correct one. The second is
  the audit trail of guesses that were actually wrong. Collapsing them back together means either
  recording a success as a failure or giving the reservation back. Both the cap filter and
  `AttemptsClaimed` take the *maximum* of the two, so sessions written before the counter existed
  stay capped by their audit list across the upgrade.
- **The rendered email is retired, and `SendOTPEmail` is nullable because of it.** It carries the code
  in cleartext, so it is `$unset` on successful verification and when the send budget is spent.
  MongoDbTokenManager stores only a hash; keeping the body next to it defeated that, and retention
  kept a four-minute code readable for 90 days along with the recipient's verified numbers and
  emails. `required` was dropped with it — the BSON deserializer does not enforce `required`, so an
  unset element would otherwise hand back null through a non-nullable property. Tests that need the
  code read it off the DevConsole log via `TestHost.LogCapture`, not out of the session.
- **One live session per address is enforced by a releasable claim, not by an index alone.**
  `LiveClaimKey` holds the `EmailId` while the session is the live one, under a **sparse unique**
  index; `ReleaseStaleClaim` `$unset`s it when the session is verified, timed out or out of attempts,
  and those conditions must keep mirroring `GetLatestSession` exactly. A unique *partial* index on
  unverified sessions is the obvious alternative and is **wrong** — measured against a real server, it
  rejects with `DuplicateKey` the successor session a user is entitled to once the predecessor times
  out, because `partialFilterExpression` cannot reference the current time. `[BsonIgnoreIfNull]` on
  `LiveClaimKey` is load-bearing: a sparse index skips *absent* fields, not BSON nulls, so writing a
  null would collide every released session under one key across all addresses.
- **Sessions are reaped by a TTL index** `SessionRetentionDays` after they expire (default 90; ≤ 0
  disables it and keeps them forever). It is a **second, single-field** index on `ExpiryTimeUTC`,
  because a TTL index cannot be compound. When amending it, the matcher must require
  `key.ElementCount == 1` or it will find the compound lookup index — which also contains
  `ExpiryTimeUTC` — and put an expiry on the index every read depends on, or drop it outright.
  `FindRetentionIndexName` is that matcher, shared by both callers. Changing the setting is applied
  with `collMod`, since MongoDB refuses to recreate an index with different options; the same
  pattern, and the same bug, exist in MongoDbTokenManager (`619679b`). Turning retention **off** has
  to *drop* the existing index, not just skip creating one — returning early left sessions being
  reaped on the old schedule while the configuration said to keep them forever.
- **The `Tokens` collection is shared** with everything else using MongoDbTokenManager against the same
  database, including SMSwitch. Dropping it invalidates their in-flight OTPs too.
- Token documents written by MongoDbTokenManager **10.0.0 cannot be deserialised by 10.2.0+** — expiry
  moved out of `TokenValue` and neither type ignores extra elements, so the read throws
  `FormatException`. `VerifyOTP` catches it; `StoredTokenCompatibilityTests` pins it and will fail if
  upstream ever adds `[BsonIgnoreExtraElements]`, at which point the workaround can go.

All three indexes — the compound `EmailId` + `ExpiryTimeUTC` lookup, the sparse unique `LiveClaimKey`,
and the `ExpiryTimeUTC` TTL — are created together on first use behind a gate
(`EmailSwitchDbService.EnsureSessionIndex`), not in the constructor, so building the DI container
never blocks on the network. A failed attempt is not cached, so a transient outage does not leave the
collection permanently unindexed.

## Configuration and startup

`EmailSwitchGeneralInitializer` reads the shared `EmailSwitchSettings` block and loads the signature
logo. **`SendGridInitializer` composes it rather than deriving from it** — deriving forced a choice
between reading the logo from disk twice and forwarding one registration to the other, and the
forwarding made every consumer of the general settings depend on SendGrid credentials existing. That
left no way to run on DevConsole alone. Do not reintroduce the inheritance.

Everything fails hard at startup with a named error: missing `SignatureLogoPath`,
`SessionTimeoutInSeconds` below 30, a `Priority` list with no recognised provider, and — only when
SendGrid is actually resolved — missing `From` or `Password`. Provider names parse case-insensitively;
unrecognised ones are logged and skipped.

**`Priority` is a `List`, not a `HashSet`, deliberately.** It is the failover order, and `HashSet<T>`
does not promise enumeration order — it happens to preserve insertion order only while nothing is
removed, which is an implementation detail rather than something to route email on. Duplicates are
collapsed explicitly in `GetPriority`, keeping the first position, so naming a provider twice does
not silently double its share of the send budget.

Nothing may take a hard dependency on `SendGridInitializer`, or a credential-free DevConsole run
breaks. Providers are only constructed when resolved through the keyed lookup.

Required config the library does not own: `MongoDbSettings:ConnectionString`, `Settings:BaseUrl` and
`Settings:FrontendUrl` all throw if absent. The host must also call `AddMongoDbServices()` and
`AddMongoDbTokenServices()` alongside `AddEmailSwitchServices()`, plus `AddEmailSwitchApiEndpoints()`
on the `WebApplication` or the signature logo 404s in every email.

## Tests

`EmailSwitch.Tests` (xUnit v3, but running under VSTest) mixes two kinds:

- **Pure tests** needing nothing: template rendering, `EmailIdentifier` normalisation, `HasNotExpired`,
  the config binders, BSON round-trips, DI registration.
- **MongoDB-backed integration tests** (`SessionStoreIntegrationTests`, `SessionRetentionTests`,
  `VerificationCapIntegrationTests`, the DevConsole end-to-end) using `EmailSwitchIntegrationFixture`
  — a uniquely named database per test, dropped on disposal. Modelled on
  `MongoDbTokenManager.Tests/MongoIntegrationFixture.cs`. They exist because the server is the thing
  under test: the `GetLatestSession` filter, whether the attempt cap actually holds under
  concurrency, what a unique index does to a session that has timed out. None of that can be
  established by reasoning over the C#, and each of those three was measured to behave differently
  from the obvious expectation.

Concurrency claims here are worth *measuring* rather than arguing about — a throwaway test that
`Assert.Fail`s with the number it observed settles in a minute what a code review will not. That is
where the "16 admitted against a cap of 3" and the `DuplicateKey`-on-successor findings came from.

`TestHost` builds a real container through `AddEmailSwitchServices()`, so tests catch missing or
mis-keyed registrations. Prefer it over hand-assembling an object graph. MongoDB is not dialled unless
a real connection string is passed — the driver connects lazily, so services resolve against an
unreachable server.

`InternalsVisibleTo` is set. When logic is worth testing, extract it to something with no database or
provider dependency, as `BuildProviderQueue` was.

## Companion packages

`HumanLanguages`, `SMSwitch`, `MongoDbService`, `MongoDbTokenManager`, `uSignIn.CommonSettings` and
`Meyn.Utilities` are all authored at https://github.com/prmeyn — read the source there instead of
inferring behaviour from names. Each `.nuspec` records the exact commit it was built from, which is
the reliable way to check whether a published package contains a given change.

EmailSwitch depends on SMSwitch only for the `MobileNumber` and `UserAgent` DTOs. SMSwitch (10.4.0 at
time of writing) carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />`; EmailSwitch
declares its own rather than inheriting it transitively, because it maps its own endpoint.

## Style and release

There is **no `.editorconfig`** in this repository. Match the surrounding files by hand: tabs for C#,
UTF-8 **with BOM**, CRLF line endings. Tooling that writes LF or strips the BOM produces noisy diffs
that git then reports as whole-file rewrites.

Releases are tag-driven: pushing a `v*.*.*` tag builds, packs and publishes to NuGet via
`.github/workflows/release.yml`. The version comes from the tag, not the csproj.

**Publishing uses NuGet Trusted Publishing (OIDC), not an API key.** `NuGet/login@v1` exchanges the
workflow's OIDC token for a short-lived key, which is why the job needs `id-token: write`. There is no
`NUGET_API_KEY` secret any more — do not reintroduce one. It depends on a Trusted Publishing policy
existing on nuget.org for the owner, repo and workflow filename; without it the login step fails at
tag time and nothing publishes. Mirrors `prmeyn/HumanLanguages@88e2aa5`.

Because the version comes from the tag, **a public API change needs a deliberate version choice** —
a patch tag will happily ship a breaking change. `EmailSwitchSession.SendOTPEmail` becoming nullable
and `EmailControls.Priority` becoming a `List` are both breaks that landed this way.

Licensed **MIT**.
