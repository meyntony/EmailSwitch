using EmailSwitch.Common.DTOs;
using EmailSwitch.Database.DTOs;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using SMSwitch.Common.DTOs;
using System.Text.RegularExpressions;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// The brute-force cap end to end, through the real service against a real MongoDB.
	///
	/// VerifyOTP used to read the session, test HasNotExpired and count the failure afterwards. That
	/// is check-then-act: guesses issued in parallel all passed the test before any of them had been
	/// recorded, so MaximumFailedAttemptsToVerify capped sequential guesses and did nothing at all
	/// against concurrent ones. With MongoDbTokenManager 10.2.0 having dropped its own attempt limit,
	/// that left a six digit code effectively unguarded to anyone who could open sockets in parallel.
	/// </summary>
	public sealed class VerificationCapIntegrationTests
	{
		private const byte MaximumFailedAttemptsToVerify = 3;
		private static readonly EmailIdentifier Email = "user@example.com";

		private sealed record Harness(ServiceProvider Provider, IMongoDatabase Database);

		private static async Task WithHarness(Func<Harness, Task> body)
		{
			var connectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING") ?? "mongodb://localhost:27017";
			var databaseName = "EmailSwitchCap_" + Guid.NewGuid();

			var settings = TestHost.BaseSettings(connectionString).WithPriority("DevConsole");
			settings["MongoDbSettings:DatabaseName"] = databaseName;
			settings["EmailSwitchSettings:Controls:MaximumFailedAttemptsToVerify"] = MaximumFailedAttemptsToVerify.ToString();

			using var provider = TestHost.Build(settings);
			var client = new MongoClient(connectionString);

			try
			{
				await body(new Harness(provider, client.GetDatabase(databaseName)));
			}
			finally
			{
				// Cleanup must still run when a test is cancelled or times out.
				await client.DropDatabaseAsync(databaseName);
			}
		}

		private static async Task<EmailSwitchSession> LoadSession(Harness harness) =>
			await harness.Database
				.GetCollection<EmailSwitchSession>(nameof(EmailSwitchSession))
				.Find(Builders<EmailSwitchSession>.Filter.Eq(session => session.EmailId, Email.ToString()))
				.FirstOrDefaultAsync();

		/// <summary>Reads the real code back out of the rendered email, as DevConsole logs it.</summary>
		private static string CodeFrom(EmailSwitchSession session) =>
			Regex.Match(session.SendOTPEmail!.PlainTextContent, @"Verification Code: (\d+)").Groups[1].Value;

		/// <summary>
		/// The regression. Sixty wrong guesses fired at once must consume the cap and no more, and the
		/// correct code must then be refused - the session is spent, exactly as it would be after three
		/// sequential wrong guesses.
		/// </summary>
		[Fact]
		public async Task Concurrent_wrong_guesses_cannot_outrun_the_cap()
		{
			await WithHarness(async harness =>
			{
				var emailSwitchService = harness.Provider.GetRequiredService<EmailSwitchService>();

				Assert.True((await emailSwitchService.SendOTP(Email, [], [], [], UserAgent.WebBrowser)).IsSent);
				var correctCode = CodeFrom(await LoadSession(harness));

				const int concurrentGuesses = 60;
				var responses = await Task.WhenAll(Enumerable
					.Range(0, concurrentGuesses)
					.Select(_ => emailSwitchService.VerifyOTP(Email, "000000")));

				Assert.All(responses, response => Assert.False(response.Verified));

				// Only the cap may have reached the token; the rest were refused before it was touched.
				var session = await LoadSession(harness);
				Assert.Equal(MaximumFailedAttemptsToVerify, session.VerificationAttemptsCount);
				Assert.Equal(MaximumFailedAttemptsToVerify, session.FailedVerificationAttemptsUTC.Count);

				// And the session really is spent: the genuine code no longer works.
				var withTheRealCode = await emailSwitchService.VerifyOTP(Email, correctCode);
				Assert.False(withTheRealCode.Verified);
				Assert.True(withTheRealCode.Expired);
			});
		}

		/// <summary>
		/// The cap must not cost the legitimate holder their code. Two wrong guesses leave one slot,
		/// and the correct code still verifies on it.
		/// </summary>
		[Fact]
		public async Task The_correct_code_still_verifies_on_the_last_remaining_attempt()
		{
			await WithHarness(async harness =>
			{
				var emailSwitchService = harness.Provider.GetRequiredService<EmailSwitchService>();

				await emailSwitchService.SendOTP(Email, [], [], [], UserAgent.WebBrowser);
				var correctCode = CodeFrom(await LoadSession(harness));

				for (var guess = 0; guess < MaximumFailedAttemptsToVerify - 1; guess++)
				{
					Assert.False((await emailSwitchService.VerifyOTP(Email, "000000")).Verified);
				}

				Assert.True((await emailSwitchService.VerifyOTP(Email, correctCode)).Verified);
			});
		}

		/// <summary>
		/// A correct guess claims a slot like any other, but it is not a failure and must not be
		/// recorded as one - the README promises the audit trail records failed verifications.
		/// </summary>
		[Fact]
		public async Task A_successful_verification_is_not_recorded_as_a_failure()
		{
			await WithHarness(async harness =>
			{
				var emailSwitchService = harness.Provider.GetRequiredService<EmailSwitchService>();

				await emailSwitchService.SendOTP(Email, [], [], [], UserAgent.WebBrowser);
				var correctCode = CodeFrom(await LoadSession(harness));

				Assert.True((await emailSwitchService.VerifyOTP(Email, correctCode)).Verified);

				var session = await LoadSession(harness);
				Assert.Equal(1, session.VerificationAttemptsCount);
				Assert.Empty(session.FailedVerificationAttemptsUTC);
				Assert.NotNull(session.SuccessfullyVerifiedTimestampUTC);
			});
		}
	}
}
