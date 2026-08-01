using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.Database.DTOs;
using EmailSwitch.EmailTemplates.DTOs;
using EmailSwitch.Services.DevConsole;
using EmailSwitch.Services.SendGrid;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using SMSwitch.Common.DTOs;
using System.Text.RegularExpressions;

namespace EmailSwitch.Tests
{
	public sealed class DevConsoleTests
	{
		private static EmailContent Content() => new()
		{
			Subject = "Email verification",
			PlainTextContent = "Verification Code: 123456",
			HtmlContent = "<h1>Verification Code: 123456</h1>"
		};

		// ------------------------------------------------------------------ the provider itself

		[Fact]
		public async Task Outside_production_the_send_succeeds_without_any_credentials()
		{
			using var provider = TestHost.Build(TestHost.BaseSettings().WithPriority("DevConsole"));

			var response = await provider.GetRequiredService<DevConsoleService>()
				.SendOTP("user@example.com", Content());

			Assert.True(response.IsSent);
			Assert.Equal(6, response.OtpLength);
		}

		/// <summary>
		/// The safety property. Reported as a failed send rather than thrown, so the provider queue
		/// falls through to a real provider if one is configured after it.
		/// </summary>
		[Fact]
		public async Task In_production_the_send_is_refused()
		{
			using var provider = TestHost.Build(
				TestHost.BaseSettings().WithPriority("DevConsole"),
				environmentName: "Production");

			var response = await provider.GetRequiredService<DevConsoleService>()
				.SendOTP("user@example.com", Content());

			Assert.False(response.IsSent);
		}

		// ------------------------------------------------------------------ registration

		[Fact]
		public void Each_provider_resolves_to_its_own_implementation()
		{
			using var provider = TestHost.Build(TestHost.BaseSettings().WithSendGrid().WithPriority("SendGrid", "DevConsole"));

			Assert.IsType<SendGridService>(provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.SendGrid));
			Assert.IsType<DevConsoleService>(provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.DevConsole));
		}

		/// <summary>
		/// The point of the provider. SendGridInitializer fails fast on missing credentials, so
		/// anything eagerly depending on it - as EmailSwitchGeneralInitializer once did, via a
		/// forwarding registration - made a credential-free local run impossible.
		/// </summary>
		[Fact]
		public void The_whole_graph_resolves_with_no_sendgrid_configuration_at_all()
		{
			var settings = TestHost.BaseSettings().WithPriority("DevConsole");
			Assert.DoesNotContain(settings.Keys, key => key.Contains("SendGrid"));

			using var provider = TestHost.Build(settings);

			Assert.NotNull(provider.GetRequiredService<EmailSwitchService>());
			Assert.NotNull(provider.GetRequiredService<EmailSwitchGeneralInitializer>());
			Assert.NotNull(provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.DevConsole));
		}

		/// <summary>
		/// The other half: SendGrid must still refuse to start without credentials. Resolving it is
		/// what triggers that, which is exactly why nothing may depend on it eagerly.
		/// </summary>
		[Fact]
		public void Resolving_sendgrid_without_credentials_still_fails_fast()
		{
			using var provider = TestHost.Build(TestHost.BaseSettings().WithPriority("DevConsole"));

			Assert.ThrowsAny<Exception>(() => provider.GetRequiredKeyedService<IServiceEmails>(EmailProvider.SendGrid));
		}

		// ------------------------------------------------------------------ end to end

		/// <summary>
		/// Send and verify with no SendGrid account, against a real MongoDB - the flow a developer
		/// gets from the appsettings.Development.json snippet in the README. The code is read back
		/// out of the rendered email, which is the same text DevConsole writes to the log.
		/// </summary>
		[Fact]
		public async Task An_otp_can_be_sent_and_verified_end_to_end_with_no_email_account()
		{
			var connectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING") ?? "mongodb://localhost:27017";
			var databaseName = "EmailSwitchDevConsole_" + Guid.NewGuid();

			var settings = TestHost.BaseSettings(connectionString).WithPriority("DevConsole");
			settings["MongoDbSettings:DatabaseName"] = databaseName;

			using var provider = TestHost.Build(settings);
			var client = new MongoClient(connectionString);

			try
			{
				var emailSwitchService = provider.GetRequiredService<EmailSwitchService>();
				EmailIdentifier email = "user@example.com";

				var sendResponse = await emailSwitchService.SendOTP(email, [], [], [], UserAgent.WebBrowser);
				Assert.True(sendResponse.IsSent);

				var session = await client.GetDatabase(databaseName)
					.GetCollection<EmailSwitchSession>(nameof(EmailSwitchSession))
					.Find(Builders<EmailSwitchSession>.Filter.Eq(s => s.EmailId, email.ToString()))
					.FirstOrDefaultAsync(TestContext.Current.CancellationToken);

				var otp = Regex.Match(session.SendOTPEmail.PlainTextContent, @"Verification Code: (\d+)").Groups[1].Value;
				Assert.Equal(6, otp.Length);

				var verifyResponse = await emailSwitchService.VerifyOTP(email, otp);
				Assert.True(verifyResponse.Verified);

				// The token is consumed, so a replay must not verify a second time.
				var replayResponse = await emailSwitchService.VerifyOTP(email, otp);
				Assert.False(replayResponse.Verified);
			}
			finally
			{
				// Deliberately not passing TestContext.Current.CancellationToken: cleanup must still
				// run when a test is cancelled or times out, otherwise the database is left behind.
#pragma warning disable xUnit1051
				await client.DropDatabaseAsync(databaseName);
#pragma warning restore xUnit1051
			}
		}
	}
}
