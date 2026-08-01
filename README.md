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
- **Covers SendGrid** as the only real provider today (more can be added)
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
| A SendGrid account | Only for real sending — not needed if you use the `DevConsole` provider. |

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
      "Priority": [ "SendGrid" ],
      "MaxRoundRobinAttempts": 2,
      "MaximumFailedAttemptsToVerify": 3,
      "SessionTimeoutInSeconds": 240
    },
    "SendGrid": {
      "From": "noreply@example.com",
      "Password": "SG.your-api-key"
    }
  }
}
```

`Settings:BaseUrl` is the public root of your API — the signature logo URL embedded in the email is
built from it, so it must be reachable by the recipient's email client.

**Keep `SendGrid:Password` out of `appsettings.json`.** Despite the name it is your SendGrid API
key; put it in user secrets, an environment variable or a key vault.

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
| `EmailSwitchSettings:Controls:Priority` | yes | — | Ordered provider list. Case-insensitive; unrecognised names are logged and skipped. |
| `EmailSwitchSettings:Controls:MaxRoundRobinAttempts` | no | `1` | Times the priority list repeats. `Priority.Count × MaxRoundRobinAttempts` is the total emails one session may send. |
| `EmailSwitchSettings:Controls:MaximumFailedAttemptsToVerify` | no | `3` | Wrong guesses before the session dies. |
| `EmailSwitchSettings:Controls:SessionTimeoutInSeconds` | no | `240` | **Minimum 30.** Below that, startup fails. |
| `EmailSwitchSettings:Controls:SessionRetentionDays` | no | `90` | Days a session is kept after it expires, then removed by a TTL index. `0` or less keeps them indefinitely. |
| `EmailSwitchSettings:SendGrid:From` | if SendGrid used | — | Sender address; also used as reply-to. |
| `EmailSwitchSettings:SendGrid:Password` | if SendGrid used | — | Your SendGrid **API key**. Keep it in a secret store. |

## Local testing without sending real email

For local development you can route messages to the `DevConsole` provider instead of SendGrid, so
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

With `DevConsole` as the only provider you can leave the `EmailSwitchSettings:SendGrid` section out
entirely — nothing constructs the SendGrid client unless SendGrid is actually used.

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
- **The code is not kept.** MongoDbTokenManager stores only a hash of it, and the rendered email that
  contains it in cleartext is dropped as soon as it can no longer be needed — on verification, or
  once the send budget is spent. It is still readable in the sessions collection for the few minutes
  in between, so treat that collection as holding secrets even though nothing retains them.
- **Read the code off the log, not the database,** if you are scripting against `DevConsole`. It is
  no longer recoverable from the stored session once the budget is spent, which with a single
  provider is immediately after the first send.
- **The logo endpoint is public** and keyed by session id, so a request to it reveals that a session
  exists. It also records each render, which doubles as an open-tracking signal.

## Contributing

We welcome contributions! If you find a bug or have an idea for improvement, please submit an issue
or a pull request on GitHub: https://github.com/prmeyn/EmailSwitch

## License

This project is licensed under the GNU AFFERO GENERAL PUBLIC LICENSE.

Happy coding! 🚀🌐📚
