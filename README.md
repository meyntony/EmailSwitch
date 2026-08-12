# EmailSwitch

**EmailSwitch** is an open-source C# class library that sends and verifies email one-time
passcodes (OTPs). You call `SendOTP` to email someone a code and `VerifyOTP` to check it; EmailSwitch
owns the session, the code, its expiry, the attempt limits and the audit trail.

Codes are generated and verified through
[MongoDbTokenManager](https://www.nuget.org/packages/MongoDbTokenManager) and everything is stored in
your own MongoDB instance, configured with
[MongoDbService](https://www.nuget.org/packages/MongoDbService). No OTP state ever leaves your
infrastructure.

## Features

- **Send and verify email OTPs** — session lifecycle, expiry and attempt limits handled for you
- **Provider failover** — an ordered priority list, retried round-robin, so a failing provider falls
  through to the next
- **`DevConsole` provider for local testing** — writes the verification email to the log instead of
  sending it, so no credentials are needed ([see below](#local-testing-without-sending-real-email))
- **Covers SendGrid, Resend and Brevo** as real providers (more can be added)
- **Audit trail in your own MongoDB** — every session, send attempt, failed verification and logo
  render is recorded

## How it works

For each email address EmailSwitch opens a *session*. Creating one mints a code through
MongoDbTokenManager, renders the email, and stores the session in MongoDB. A send budget is built
from your `Priority` list repeated `MaxRoundRobinAttempts` times; each send attempt spends one slot,
and a failed attempt falls through to the next provider.

While a session is live, calling `SendOTP` again **reuses it** — the recipient gets the same code,
not a new one. The session ends when it is verified, when `SessionTimeoutInSeconds` elapses, or
after `MaximumFailedAttemptsToVerify` wrong guesses.

Verification is atomic: of two concurrent requests submitting the same correct code, exactly one
succeeds. A wrong guess leaves the code usable, so an attacker cannot lock the legitimate holder out
by guessing.

Sessions are kept as an audit record for `SessionRetentionDays` after they expire — 90 days by
default — and are then removed automatically by a MongoDB TTL index. Expired *tokens* are cleaned up
by MongoDbTokenManager separately.

The rendered email is held on the session only while a resend could still need it, and is dropped as
soon as the code is verified or the send budget is spent. It carries the code in cleartext, so it
must not sit in the audit record for the retention period — what survives is the session's
timestamps and its send attempts, not the code or the contact details the body listed.

## Getting started

### 1. Install

```bash
dotnet add package EmailSwitch
```

### 2. Prerequisites

| Requirement | Why |
| --- | --- |
| .NET 10.0 | The package targets `net10.0`. |
| An ASP.NET Core host | EmailSwitch maps its own minimal-API endpoint for the email signature logo, so it references the ASP.NET Core shared framework. |
| MongoDB | Sessions and tokens are stored in your instance. MongoDbTokenManager creates a TTL index to clean up expired tokens. |
| A SendGrid, Resend or Brevo account | Only for real sending — not needed if you use the `DevConsole` provider. All three need a verified sender or domain before they will deliver to anyone, and their free tiers each carry a limit that matters on an OTP path; see [Worth knowing](#worth-knowing). |

### 3. Configure

Every section below is required. EmailSwitch fails at startup with a named error rather than
misbehaving later, so a missing key is reported clearly.

```json
{
  "MongoDbSettings": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "MyApp"
  },
  "Settings": {
    "BaseUrl": "https://api.example.com",
    "FrontendUrl": "https://app.example.com"
  },
  "EmailSwitchSettings": {
    "OtpLength": 6,
    "SignatureLogoPath": "wwwroot/logo.png",
    "Controls": {
      "Priority": [ "Resend", "SendGrid" ],
      "MaxRoundRobinAttempts": 2,
      "MaximumFailedAttemptsToVerify": 3,
      "SessionTimeoutInSeconds": 240
    },
    "Resend": {
      "From": "noreply@example.com",
      "ApiKey": "re_your-api-key"
    },
    "Brevo": {
      "From": "noreply@example.com",
      "ApiKey": "xkeysib-your-api-key"
    },
    "SendGrid": {
      "From": "noreply@example.com",
      "Password": "SG.your-api-key"
    }
  }
}
```

You only need a section for the providers you actually name in `Priority`. A section for a provider
you do not use is never read, and a provider you do name but do not configure fails startup.

`Settings:BaseUrl` is the public root of your API — the signature logo URL embedded in the email is
built from it, so it must be reachable by the recipient's email client.

**Keep your provider keys out of `appsettings.json`** — `Resend:ApiKey` and `SendGrid:Password`
both. Put them in user secrets, an environment variable or a key vault. The two are named
differently because despite its name `SendGrid:Password` *is* an API key, and renaming a
configuration key would break every existing consumer on upgrade; newer providers use the accurate
name rather than inheriting the mistake.

### 4. Register the services

EmailSwitch depends on MongoDbService and MongoDbTokenManager, and **you must register both** — it
does not do it for you:

```csharp
using EmailSwitch;
using MongoDbService;
using MongoDbTokenManager;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMongoDbServices();
builder.Services.AddMongoDbTokenServices();
builder.Services.AddEmailSwitchServices();
```

### 5. Map the endpoints

Required for the signature logo in the email to render — without it the image 404s:

```csharp
var app = builder.Build();

app.AddEmailSwitchApiEndpoints();

app.Run();
```

This maps `GET /emailswitch/logo/{sessionId}`, which serves the file at `SignatureLogoPath`. The
endpoint is public and unauthenticated, because email clients fetch it with no credentials.

### 6. Send and verify

```csharp
using EmailSwitch;
using EmailSwitch.Common.DTOs;
using HumanLanguages;
using SMSwitch.Common.DTOs;

app.MapPost("/send", async (EmailSwitchService emailSwitch, string email) =>
{
    var response = await emailSwitch.SendOTP(
        email: email,                                     // string converts implicitly
        verifiedMobileNumbers: [],
        verifiedEmails: [],
        preferredLanguageIsoCodeList: [new LanguageIsoCode(LanguageId.en)],
        userAgent: UserAgent.WebBrowser);

    return response.IsSent
        ? Results.Ok(new { response.OtpLength, response.ExpiryDateTimeOffset })
        : Results.Problem("Could not send the verification code.");
});

app.MapPost("/verify", async (EmailSwitchService emailSwitch, string email, string code) =>
{
    var response = await emailSwitch.VerifyOTP(email, code);

    if (response.Verified) return Results.Ok();

    // Expired means there is no live session: request a new code rather than retrying this one.
    return response.Expired
        ? Results.Problem("That code has expired. Please request a new one.")
        : Results.Problem("That code is not correct.");
});
```

## API

### `SendOTP`

| Parameter | Notes |
| --- | --- |
| `email` | `EmailIdentifier`. A `string` converts implicitly. |
| `verifiedMobileNumbers` | `MobileNumber[]` from [SMSwitch](https://www.nuget.org/packages/SMSwitch). Listed in the email body as a "these are the contacts we already know for you" cue. Pass `[]` if you have none. |
| `verifiedEmails` | `EmailIdentifier[]`, shown for the same reason. Pass `[]` if you have none. |
| `preferredLanguageIsoCodeList` | `HashSet<LanguageIsoCode>`. The first entry wins. Only **English and Danish** subjects are translated today; anything else falls back to English. |
| `userAgent` | Accepted for parity with SMSwitch. Not currently used when rendering the email. |

Returns `EmailSwitchResponseSendOTP`:

| Field | Meaning |
| --- | --- |
| `IsSent` | Whether a provider accepted the message. |
| `OtpLength` | Digits in the code, for sizing your input field. |
| `ExpiryDateTimeOffset` | When the session expires, for a countdown. |

### `VerifyOTP`

Takes the address and the code the user typed. Returns `EmailSwitchResponseVerifyOTP`:

| Field | Meaning |
| --- | --- |
| `Verified` | The code was correct and has now been consumed. |
| `Expired` | There was no live session — it timed out, ran out of attempts, was already used, or could not be read. Ask the user to request a new code rather than retry. |

### Email addresses

`EmailIdentifier` normalises before using an address as the session key: it lowercases, strips
plus-addressing, and collapses dots for `gmail.com`. So `J.o.h.n+promo@Gmail.com` and
`john@gmail.com` are one inbox and share a session. The address you passed in is preserved verbatim
for the actual send.

## Configuration reference

| Key | Required | Default | Notes |
| --- | --- | --- | --- |
| `MongoDbSettings:ConnectionString` | yes | — | |
| `MongoDbSettings:DatabaseName` | no | `Untitled-MongoDbService` | |
| `Settings:BaseUrl` | yes | — | Public API root; the logo URL is built from it. |
| `Settings:FrontendUrl` | yes | — | Required by the shared settings package. |
| `EmailSwitchSettings:OtpLength` | no | `6` | |
| `EmailSwitchSettings:SignatureLogoPath` | yes | — | Read once at startup. `.png`, `.jpg`, `.gif`, `.webp` and `.svg` get a matching content type. |
| `EmailSwitchSettings:Controls:Priority` | yes | — | Ordered provider list — the order is the failover order. Case-insensitive; unrecognised names are logged and skipped, and a name repeated is kept once, in its first position. |
| `EmailSwitchSettings:Controls:MaxRoundRobinAttempts` | no | `1` | Times the priority list repeats. `Priority.Count × MaxRoundRobinAttempts` is the total emails one session may send. |
| `EmailSwitchSettings:Controls:MaximumFailedAttemptsToVerify` | no | `3` | Wrong guesses before the session dies. |
| `EmailSwitchSettings:Controls:SessionTimeoutInSeconds` | no | `240` | **Minimum 30.** Below that, startup fails. |
| `EmailSwitchSettings:Controls:SessionRetentionDays` | no | `90` | Days a session is kept after it expires, then removed by a TTL index. `0` or less keeps them indefinitely. |
| `EmailSwitchSettings:SendGrid:From` | if SendGrid used | — | Sender address; also used as reply-to. |
| `EmailSwitchSettings:SendGrid:Password` | if SendGrid used | — | Your SendGrid **API key**. Keep it in a secret store. |
| `EmailSwitchSettings:Resend:From` | if Resend used | — | Sender address; also used as reply-to. Must be on a domain verified in Resend, or delivery is limited to your own account address. |
| `EmailSwitchSettings:Resend:ApiKey` | if Resend used | — | Your Resend API key (`re_…`). Keep it in a secret store. Named `ApiKey`, not `Password` — see above. |
| `EmailSwitchSettings:Brevo:From` | if Brevo used | — | Sender address; also used as reply-to. Must be a verified sender or an authenticated domain in Brevo. |
| `EmailSwitchSettings:Brevo:ApiKey` | if Brevo used | — | Your Brevo API v3 key (`xkeysib-…`). Keep it in a secret store. |

Resend and Brevo are both reached over plain HTTPS with no SDK, so neither adds a package dependency
to your app. Each request times out after 10 seconds, because a send sits on the login path with a
user waiting on it.

## Local testing without sending real email

For local development you can route messages to the `DevConsole` provider instead of a real one, so
no mail is sent and no credentials are needed. The rendered email — including the verification
code — is written to the log, and because codes are generated and verified through
MongoDbTokenManager in your own MongoDB instance, the full `SendOTP` → `VerifyOTP` flow works end to
end.

Put this in your `appsettings.Development.json`:

```json
{
  "EmailSwitchSettings": {
    "Controls": {
      "Priority": [ "DevConsole" ]
    }
  }
}
```

With `DevConsole` as the only provider you can leave every other provider section out entirely —
nothing constructs a provider unless it is actually named in `Priority` and resolved.

As a safety measure the `DevConsole` provider refuses to operate when the app runs in the
`Production` environment: it logs a critical error and reports the send as failed, so the provider
queue falls through to a real provider if one is configured after it.

> The verification code is written to your logs in plain text. Never enable `DevConsole` anywhere
> real users receive codes, and keep those logs out of shared sinks.

## Worth knowing

- **A resend returns the same code.** While a session is live, `SendOTP` reuses it rather than
  minting a new code.
- **Sends are budgeted.** Once `Priority.Count × MaxRoundRobinAttempts` attempts are spent,
  `SendOTP` returns `IsSent = false`. The code already delivered stays verifiable until the session
  expires.
- **Sessions are the audit trail and expire on their own schedule.** `SessionRetentionDays` (90 by
  default) governs how long they survive past expiry. Sessions hold the verified email address, so
  set this to whatever your retention policy allows rather than leaving it unbounded. Note a TTL
  index gives time-based expiry, not erasure of one person's data on request.
- **The code is not kept — in *your* storage.** MongoDbTokenManager stores only a hash of it, and the
  rendered email that contains it in cleartext is dropped as soon as it can no longer be needed — on
  verification, or once the send budget is spent. It is still readable in the sessions collection for
  the few minutes in between, so treat that collection as holding secrets even though nothing retains
  them. This guarantee stops at your infrastructure: whichever provider you route through is handed
  the rendered email, code included, and retains it on its own schedule and in its own dashboard.
  That is true of SendGrid and Resend alike, and it is not something EmailSwitch can shorten.
- **Read the code off the log, not the database,** if you are scripting against `DevConsole`. It is
  no longer recoverable from the stored session once the budget is spent, which with a single
  provider is immediately after the first send.
- **The logo endpoint is public** and keyed by session id, so a request to it reveals that a session
  exists. It also records each render, which doubles as an open-tracking signal.

### If you use Resend

An OTP path is not a newsletter: a provider limit that would be a minor annoyance elsewhere locks
people out of their accounts here. These are the ones worth knowing before you point `Priority` at
Resend. All of them were checked against Resend's own documentation on 10-08-2026 — verify them
against the current docs rather than trusting this list.

- **The free tier caps you at 100 emails/day and 3,000/month, on one verified domain.** Past the cap
  Resend answers `429`, EmailSwitch reports `IsSent = false`, and nobody can log in until the day
  rolls over. ([quotas and limits](https://resend.com/docs/knowledge-base/account-quotas-and-limits))
- **The rate limit is 10 requests/second per team, shared across every API key** — not per key. A
  burst of logins, or another service on the same team, can push the OTP path into `429`.
- **An unverified domain works in development and fails in production.** Until you verify a domain
  you can only send from `onboarding@resend.dev`, and only to your own account's address. Every other
  recipient gets a `403`, which EmailSwitch logs with the response body.
  ([403 on resend.dev](https://resend.com/docs/knowledge-base/403-error-resend-dev-domain))
- **Region selection is about where mail is sent *from*, not where data lives.** Resend can dispatch
  from `us-east-1`, `eu-west-1` or `sa-east-1`, but its documentation states that account data, email
  metadata and logs are stored in the United States regardless. Resend publishes a DPA, states GDPR
  compliance and holds an EU-US Data Privacy Framework certification; if you are transferring
  personal data out of the EU, read those and the current subprocessor list yourself rather than
  taking this paragraph as a compliance assessment.
  ([choosing a region](https://resend.com/docs/dashboard/domains/regions),
  [DPA](https://resend.com/legal/dpa))
- **Failover is your mitigation for all of the above.** With `"Priority": [ "Resend", "SendGrid" ]`
  a `429` or `403` from Resend falls through to the next provider within the same send budget, and
  the recipient still gets the code they asked for.

### If you use Brevo

Checked against Brevo's own documentation on 12-08-2026; verify against the current docs rather than
trusting this list.

- **Do not run OTP on the free plan.** Free-plan Brevo stamps a "Sent with Brevo" sticker into the
  body of every email it sends. On a verification email that puts third-party branding in front of
  exactly the users you want to be suspicious of anything unexpected, and it is not something
  EmailSwitch can strip. Removing it requires a paid plan or a paid add-on — confirm the current
  terms with Brevo directly.
  ([free plan limits](https://help.brevo.com/hc/en-us/articles/208580669-FAQs-What-are-the-limits-of-the-Free-plan))
- **The free plan also caps you at 300 emails/day.** Past it sends fail and nobody can log in.
- **The rate limit is generous** — roughly 1,000 requests/second on the send endpoint, with
  `x-sib-ratelimit-limit`, `-remaining` and `-reset` response headers. This is the one place Brevo is
  clearly better suited to a login path than Resend's 10 requests/second per team.
  ([rate limits](https://developers.brevo.com/docs/api-limits))
- **Brevo states that it stores data in the EU** — its processing and database servers on its own
  hardware and Google Cloud, a GDPR-compliant DPA, and no Standard Contractual Clauses needed for
  standard deployments. That is a real contrast with Resend, whose account data and logs sit in the
  United States whichever region you send from, and for an EU deployment it may be the deciding
  factor between the two. This is Brevo's stated position, not an assessment: read the current DPA
  and subprocessor list yourself before relying on it.
  ([data storage location](https://help.brevo.com/hc/en-us/articles/360001005510-Data-storage-location),
  [DPA](https://www.brevo.com/legal/))
- **Brevo authenticates with an `api-key` header**, not a bearer token, and answers `201` rather than
  `200` on a successful send. Both are handled; they are noted only because a proxy or gateway in
  front of it that normalises either will break sends.

## Contributing

We welcome contributions! If you find a bug or have an idea for improvement, please submit an issue
or a pull request on GitHub: https://github.com/prmeyn/EmailSwitch

## License

This project is licensed under the MIT License.

Happy coding! 🚀🌐📚
