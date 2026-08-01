# EmailSwitch

**EmailSwitch** is an open-source C# class library that provides a wrapper around existing services that are used to verify emails and send messages.
The service stores information in a MongoDb database that you configure using the package [MongoDbService](https://www.nuget.org/packages/MongoDbService) 
## Features

- Covers only SendGrid as of today (possible to cover more if needed)
- **`DevConsole` provider for local testing** — writes the verification email to the log instead of sending it ([see below](#local-testing-without-sending-real-email))
- Usage information is stored in your own MongoDB instance for audit reasons


## Contributing

We welcome contributions! If you find a bug, have an idea for improvement, please submit an issue or a pull request on GitHub.

## Getting Started

### [NuGet Package](https://www.nuget.org/packages/EmailSwitch)

To include **EmailSwitch** in your project, [install the NuGet package](https://www.nuget.org/packages/EmailSwitch):

```bash
dotnet add package EmailSwitch
```
Then in your `appsettings.json` add the following sample configuration and change the values to match the details of your credentials to the various services.
```json
  "EmailSwitchSettings": {
  "OtpLength": 6,
  "SignatureLogoPath": "wwwroot/logo.png",
  "Controls": {
    "MaxRoundRobinAttempts": 2,
    "Priority": [ "SendGrid" ],
    "MaximumFailedAttemptsToVerify": 3,
    "SessionTimeoutInSeconds": 240
  },
  "SendGrid": {
    "From": "abc@xyz.com",
    "Password": "MovedToSecret"
  }
}
  ```

After the above is done, you can just Dependency inject the `EmailSwitch` in your C# class.

#### For example:



```csharp
TODO

```

## Local testing without sending real email

For local development you can route messages to the `DevConsole` provider instead of SendGrid, so
no mail is sent and no credentials are needed. The rendered email — including the verification
code — is written to the log, and because codes are generated and verified through
[MongoDbTokenManager](https://www.nuget.org/packages/MongoDbTokenManager) in your own MongoDB
instance, the full `SendOTP` → `VerifyOTP` flow works end to end.

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

### GitHub Repository
Visit our GitHub repository for the latest updates, documentation, and community contributions.
https://github.com/prmeyn/EmailSwitch


## License

This project is licensed under the GNU AFFERO GENERAL PUBLIC LICENSE

Happy coding! 🚀🌐📚



