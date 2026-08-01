using EmailSwitch.Common;
using EmailSwitch.Database;
using EmailSwitch.Services.SendGrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using MongoDbService;
using MongoDbTokenManager.Database;
using uSignIn.CommonSettings.Settings;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// Per-test MongoDB scope: builds an <see cref="EmailSwitchDbService"/> against a uniquely named
	/// database and drops it on disposal, so tests cannot interfere with one another. Requires a
	/// reachable server; set MONGODB_CONNECTION_STRING to point somewhere other than localhost.
	///
	/// Modelled on MongoDbTokenManager.Tests/MongoIntegrationFixture.cs.
	/// </summary>
	internal sealed class EmailSwitchIntegrationFixture : IAsyncDisposable
	{
		private readonly MongoService _mongoService;
		private readonly string _databaseName;

		public EmailSwitchDbService DbService { get; }
		public MongoDbTokenService TokenService { get; }

		/// <summary>The raw database, so tests can read documents the service does not expose.</summary>
		public IMongoDatabase Database => _mongoService.Database;

		public EmailSwitchIntegrationFixture(byte maximumFailedAttemptsToVerify = 3)
		{
			var connectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING") ?? "mongodb://localhost:27017";
			_databaseName = "EmailSwitchTestDb_" + Guid.NewGuid();

			var configuration = new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["MongoDbSettings:ConnectionString"] = connectionString,
					["MongoDbSettings:DatabaseName"] = _databaseName,

					["Settings:BaseUrl"] = "https://api.example.com",
					["Settings:FrontendUrl"] = "https://app.example.com",

					["EmailSwitchSettings:OtpLength"] = "6",
					["EmailSwitchSettings:SignatureLogoPath"] = "logo.png",
					["EmailSwitchSettings:SendGrid:From"] = "noreply@example.com",
					["EmailSwitchSettings:SendGrid:Password"] = "SG.fake-api-key",
					["EmailSwitchSettings:Controls:Priority:0"] = "SendGrid",
					["EmailSwitchSettings:Controls:MaximumFailedAttemptsToVerify"] = maximumFailedAttemptsToVerify.ToString(),
					["EmailSwitchSettings:Controls:MaxRoundRobinAttempts"] = "1",
					["EmailSwitchSettings:Controls:SessionTimeoutInSeconds"] = "240"
				})
				.Build();

			_mongoService = new MongoService(configuration, NullLogger<MongoService>.Instance);
			TokenService = new MongoDbTokenService(_mongoService);

			DbService = new EmailSwitchDbService(
				_mongoService,
				new EmailSwitchInitializer(configuration, NullLogger<EmailSwitchInitializer>.Instance),
				TokenService,
				new EmailSwitchGeneralInitializer(configuration, NullLogger<EmailSwitchGeneralInitializer>.Instance),
				new SettingsService(configuration, NullLogger<SettingsService>.Instance),
				NullLogger<EmailSwitchDbService>.Instance);
		}

		public async ValueTask DisposeAsync()
		{
			// Deliberately unconditional: cleanup must still run when a test is cancelled or times
			// out, otherwise the database is left behind.
			await _mongoService.Database.Client.DropDatabaseAsync(_databaseName);
		}
	}
}
