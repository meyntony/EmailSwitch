using EmailSwitch.Common;
using EmailSwitch.Database;
using EmailSwitch.Services.SendGrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDbService;
using MongoDbTokenManager.Database;
using uSignIn.CommonSettings.Settings;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// VerifyOTP had no try/catch, unlike SendOTP, so anything the datastore threw escaped the
	/// library as a server error. That matters most on the MongoDbTokenManager 10.0.0 to 10.2.0
	/// upgrade, where a token document written by the old version raises a FormatException on read -
	/// see <see cref="StoredTokenCompatibilityTests"/>.
	///
	/// The whole object graph is built for real against an unreachable MongoDB. MongoClient does not
	/// connect eagerly, so construction succeeds and the failure lands where it matters: on the
	/// query inside VerifyOTP.
	/// </summary>
	public sealed class VerifyOtpFailureContainmentTests
	{
		private static EmailSwitchService CreateServiceWithUnreachableDatabase()
		{
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				// Unroutable, with the timeouts pulled right down so the test fails fast.
				["MongoDbSettings:ConnectionString"] = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=150&connectTimeoutMS=150&socketTimeoutMS=150",
				["MongoDbSettings:DatabaseName"] = "EmailSwitchTests",
				["MongoDbSettings:ConnectionRecordRetentionDays"] = "0",

				["Settings:BaseUrl"] = "https://api.example.com",
				["Settings:FrontendUrl"] = "https://app.example.com",

				["EmailSwitchSettings:OtpLength"] = "6",
				["EmailSwitchSettings:SignatureLogoPath"] = "logo.png",
				["EmailSwitchSettings:SendGrid:From"] = "noreply@example.com",
				["EmailSwitchSettings:SendGrid:Password"] = "SG.fake-api-key",
				["EmailSwitchSettings:Controls:Priority:0"] = "SendGrid",
				["EmailSwitchSettings:Controls:MaximumFailedAttemptsToVerify"] = "3",
				["EmailSwitchSettings:Controls:SessionTimeoutInSeconds"] = "240"
			}).Build();

			var mongoService = new MongoService(configuration, NullLogger<MongoService>.Instance);
			var sendGridInitializer = new SendGridInitializer(configuration, NullLogger<SendGridInitializer>.Instance);
			var tokenService = new MongoDbTokenService(mongoService);

			var dbService = new EmailSwitchDbService(
				mongoService,
				new EmailSwitchInitializer(configuration),
				tokenService,
				sendGridInitializer,
				new SettingsService(configuration, NullLogger<SettingsService>.Instance),
				NullLogger<EmailSwitchDbService>.Instance);

			return new EmailSwitchService(
				new EmailSwitchInitializer(configuration),
				new SendGridService(sendGridInitializer, NullLogger<SendGridService>.Instance),
				dbService,
				tokenService,
				NullLogger<EmailSwitchService>.Instance);
		}

		[Fact]
		public async Task VerifyOTP_reports_a_failed_verification_instead_of_throwing_when_the_datastore_fails()
		{
			var service = CreateServiceWithUnreachableDatabase();

			var response = await service.VerifyOTP("user@example.com", "123456");

			Assert.False(response.Verified);
		}

		/// <summary>
		/// A caller that cannot be told the code was wrong should at least be told to ask for a new
		/// one, rather than reading the always-false Expired the property used to return.
		/// </summary>
		[Fact]
		public async Task VerifyOTP_reports_the_session_as_expired_when_it_cannot_be_read()
		{
			var service = CreateServiceWithUnreachableDatabase();

			var response = await service.VerifyOTP("user@example.com", "123456");

			Assert.True(response.Expired);
		}

		/// <summary>SendOTP already contained its failures; this guards against a regression.</summary>
		[Fact]
		public async Task SendOTP_reports_a_failed_send_instead_of_throwing_when_the_datastore_fails()
		{
			var service = CreateServiceWithUnreachableDatabase();

			var response = await service.SendOTP("user@example.com", [], [], [], SMSwitch.Common.DTOs.UserAgent.WebBrowser);

			Assert.False(response.IsSent);
		}
	}
}
