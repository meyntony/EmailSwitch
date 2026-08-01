using Microsoft.Extensions.DependencyInjection;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// VerifyOTP had no try/catch, unlike SendOTP, so anything the datastore threw escaped the
	/// library as a server error. That matters most on the MongoDbTokenManager 10.0.0 to 10.2.0
	/// upgrade, where a token document written by the old version raises a FormatException on read -
	/// see <see cref="StoredTokenCompatibilityTests"/>.
	///
	/// Resolved from a real container built against an unreachable MongoDB. The driver does not
	/// connect eagerly, so resolution succeeds and the failure lands where it matters: on the query.
	/// </summary>
	public sealed class VerifyOtpFailureContainmentTests
	{
		private static EmailSwitchService CreateServiceWithUnreachableDatabase() =>
			TestHost
				.Build(TestHost.BaseSettings().WithSendGrid().WithPriority("SendGrid"))
				.GetRequiredService<EmailSwitchService>();

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

		/// <summary>
		/// A caller sizes its code input off OtpLength, so a failed send handing back zero would have
		/// it render a zero-length field. SendGridService already populated this on its own failure
		/// paths; the ones in EmailSwitchService were simply inconsistent with it.
		/// </summary>
		[Fact]
		public async Task A_failed_send_still_reports_the_otp_length()
		{
			var service = CreateServiceWithUnreachableDatabase();

			var response = await service.SendOTP("user@example.com", [], [], [], SMSwitch.Common.DTOs.UserAgent.WebBrowser);

			Assert.False(response.IsSent);
			Assert.Equal(6, response.OtpLength);
		}
	}
}
