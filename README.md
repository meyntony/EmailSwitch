# EmailSwitch

**EmailSwitch** is an open-source C# class library that provides a wrapper around existing services that are used to verify emails and send messages.
The service stores information in a MongoDb database that you configure using the package [MongoDbService](https://www.nuget.org/packages/MongoDbService) 
## Features

- Covers only SendGrid as of today (possible to cover more if needed)
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

### GitHub Repository
Visit our GitHub repository for the latest updates, documentation, and community contributions.
https://github.com/prmeyn/EmailSwitch


## License

This project is licensed under the GNU AFFERO GENERAL PUBLIC LICENSE

Happy coding! 🚀🌐📚



