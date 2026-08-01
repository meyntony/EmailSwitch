using EmailSwitch.Common;
using EmailSwitch.Services.SendGrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailSwitch.Tests
{
	public sealed class EmailSwitchGeneralInitializerTests
	{
		private static EmailSwitchGeneralInitializer Create(string? signatureLogoPath, string? otpLength = "6")
		{
			var values = new Dictionary<string, string?>
			{
				["EmailSwitchSettings:SignatureLogoPath"] = signatureLogoPath,
				["EmailSwitchSettings:OtpLength"] = otpLength
			};

			return new EmailSwitchGeneralInitializer(
				new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
				NullLogger<EmailSwitchGeneralInitializer>.Instance);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void A_missing_signature_logo_path_is_rejected(string? signatureLogoPath)
		{
			var exception = Assert.Throws<ArgumentException>(() => Create(signatureLogoPath));

			Assert.Contains("SignatureLogoPath", exception.Message);
		}

		/// <summary>
		/// The logo endpoint has to send a media type or email clients will not render the image, and
		/// the only thing it can derive one from is the configured file extension.
		/// </summary>
		[Theory]
		[InlineData("logo.png", "image/png")]
		[InlineData("wwwroot/LOGO.PNG", "image/png")]
		[InlineData("logo.jpg", "image/jpeg")]
		[InlineData("logo.jpeg", "image/jpeg")]
		[InlineData("logo.gif", "image/gif")]
		[InlineData("logo.webp", "image/webp")]
		[InlineData("logo.svg", "image/svg+xml")]
		[InlineData("logo.bmp", "application/octet-stream")]
		[InlineData("logo", "application/octet-stream")]
		public void The_logo_content_type_follows_the_file_extension(string signatureLogoPath, string expectedContentType)
		{
			var initializer = Create(signatureLogoPath);

			Assert.Equal(expectedContentType, initializer.EmailSwitchGeneralSettings.SignatureLogoContentType);
		}

		/// <summary>
		/// An unreadable logo is logged and left empty rather than failing startup - the endpoint
		/// turns the empty result into a 404.
		/// </summary>
		[Fact]
		public void An_unreadable_logo_leaves_the_bytes_empty_without_throwing()
		{
			var initializer = Create("this-file-does-not-exist.png");

			Assert.Empty(initializer.EmailSwitchGeneralSettings.SignatureLogoBytes);
		}

		[Theory]
		[InlineData("4", 4)]
		[InlineData(null, 6)]
		[InlineData("not-a-number", 6)]
		public void The_otp_length_falls_back_to_six(string? configured, byte expected)
		{
			var initializer = Create("logo.png", otpLength: configured);

			Assert.Equal(expected, initializer.EmailSwitchGeneralSettings.OtpLength);
		}
	}

	public sealed class SendGridInitializerTests
	{
		private static SendGridInitializer Create(string? from = "noreply@example.com", string? password = "SG.fake-api-key")
		{
			var values = new Dictionary<string, string?>
			{
				["EmailSwitchSettings:SignatureLogoPath"] = "logo.png",
				["EmailSwitchSettings:OtpLength"] = "6",
				["EmailSwitchSettings:SendGrid:From"] = from,
				["EmailSwitchSettings:SendGrid:Password"] = password
			};

			return new SendGridInitializer(
				new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
				NullLogger<SendGridInitializer>.Instance);
		}

		/// <summary>
		/// Missing credentials used to be caught and logged, which left SendGridSettings null and
		/// turned every later send into a swallowed NullReferenceException - email silently never
		/// went out. Startup must fail loudly instead.
		/// </summary>
		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("  ")]
		public void A_missing_sender_address_fails_startup(string? from)
		{
			var exception = Assert.Throws<ArgumentException>(() => Create(from: from));

			Assert.Contains("From", exception.Message);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("  ")]
		public void A_missing_api_key_fails_startup(string? password)
		{
			var exception = Assert.Throws<ArgumentException>(() => Create(password: password));

			Assert.Contains("Password", exception.Message);
		}

		/// <summary>The failure message must name the setting without quoting its value.</summary>
		[Fact]
		public void The_failure_message_does_not_leak_the_api_key()
		{
			const string apiKey = "SG.super-secret-value";

			var exception = Assert.Throws<ArgumentException>(() => Create(from: null, password: apiKey));

			Assert.DoesNotContain(apiKey, exception.Message);
		}

		[Fact]
		public void A_complete_configuration_initializes()
		{
			var initializer = Create();

			Assert.Equal("noreply@example.com", initializer.SendGridSettings.SendGridPrivateSettings.From);
			Assert.NotNull(initializer.SendGridClient);
			Assert.Equal(6, initializer.SendGridSettings.OtpLength);
		}
	}

	public sealed class EmailSwitchInitializerTests
	{
		private static EmailSwitchInitializer Create(
			string[]? priority = null,
			string? maximumFailedAttemptsToVerify = null,
			string? sessionTimeoutInSeconds = null,
			string? maxRoundRobinAttempts = null)
		{
			var values = new Dictionary<string, string?>
			{
				["EmailSwitchSettings:Controls:MaximumFailedAttemptsToVerify"] = maximumFailedAttemptsToVerify,
				["EmailSwitchSettings:Controls:SessionTimeoutInSeconds"] = sessionTimeoutInSeconds,
				["EmailSwitchSettings:Controls:MaxRoundRobinAttempts"] = maxRoundRobinAttempts
			};

			foreach (var (value, index) in (priority ?? ["SendGrid"]).Select((value, index) => (value, index)))
			{
				values[$"EmailSwitchSettings:Controls:Priority:{index}"] = value;
			}

			return new EmailSwitchInitializer(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
		}

		[Fact]
		public void The_provider_priority_is_read_from_configuration()
		{
			Assert.Equal([EmailProvider.SendGrid], Create().EmailControls.Priority);
		}

		/// <summary>Unknown provider names are skipped rather than crashing startup.</summary>
		[Fact]
		public void Unknown_provider_names_are_ignored()
		{
			var initializer = Create(priority: ["Mailgun", "SendGrid", "Postmark"]);

			Assert.Equal([EmailProvider.SendGrid], initializer.EmailControls.Priority);
		}

		/// <summary>
		/// An empty priority list would leave SendOTP with nothing to try, so it must fail at startup
		/// rather than return IsSent = false forever.
		/// </summary>
		[Fact]
		public void A_priority_list_with_no_recognised_provider_is_rejected()
		{
			Assert.ThrowsAny<Exception>(() => Create(priority: ["Mailgun"]));
		}

		[Fact]
		public void A_missing_priority_section_is_rejected()
		{
			Assert.ThrowsAny<Exception>(() => Create(priority: []));
		}

		[Fact]
		public void The_controls_fall_back_to_documented_defaults()
		{
			var controls = Create().EmailControls;

			Assert.Equal(3, controls.MaximumFailedAttemptsToVerify);
			Assert.Equal(240, controls.SessionTimeoutInSeconds);
			Assert.Equal(1, controls.MaxRoundRobinAttempts);
		}

		[Fact]
		public void The_controls_are_read_from_configuration_when_present()
		{
			var controls = Create(
				maximumFailedAttemptsToVerify: "5",
				sessionTimeoutInSeconds: "600",
				maxRoundRobinAttempts: "2").EmailControls;

			Assert.Equal(5, controls.MaximumFailedAttemptsToVerify);
			Assert.Equal(600, controls.SessionTimeoutInSeconds);
			Assert.Equal(2, controls.MaxRoundRobinAttempts);
		}
	}
}
