using EmailSwitch.Common;
using Microsoft.Extensions.Logging;

namespace EmailSwitch.Services.Brevo
{
	/// <summary>
	/// Composes <see cref="EmailSwitchGeneralInitializer"/> rather than deriving from it, for the same
	/// reason <c>SendGridInitializer</c> and <c>ResendInitializer</c> do: a forwarding registration
	/// would make every consumer of the general settings depend on Brevo credentials being present,
	/// which leaves no way to run on the DevConsole provider alone.
	/// </summary>
	public sealed class BrevoInitializer
	{
		/// <summary>
		/// The named client registered in <c>ServiceCollectionExtensions</c>. Registering it there
		/// constructs nothing and reads no configuration, so it does not disturb the rule that a
		/// provider is only built when it is resolved.
		/// </summary>
		public const string HttpClientName = "EmailSwitch.Brevo";

		/// <summary>
		/// Trailing slash is load-bearing: the send endpoint is appended as a relative path, and
		/// without it Uri resolution drops the <c>/v3</c> segment.
		/// </summary>
		internal static readonly Uri BaseAddress = new("https://api.brevo.com/v3/");

		/// <summary>
		/// A send sits on the login path with a caller waiting on it. HttpClient's own 100 second
		/// default is long past the point where the user has given up and asked for another code.
		/// </summary>
		internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

		private readonly IHttpClientFactory _httpClientFactory;

		internal readonly BrevoSettings BrevoSettings;

		/// <summary>
		/// Brevo authenticates with a custom <c>api-key</c> header rather than <c>Authorization</c>,
		/// so there is no AuthenticationHeaderValue to cache - the raw key is carried here and added
		/// per request. Setting it on the named client's default headers would need the credential
		/// while the container is still being built, which is the eager read that makes a
		/// DevConsole-only host impossible to start.
		/// </summary>
		internal readonly string ApiKey;

		/// <summary>
		/// Shared secret carried in the webhook path, or null when none is configured.
		///
		/// Brevo does not sign its webhooks - Resend uses Svix HMAC and SendGrid uses ECDSA, but Brevo
		/// offers only IP allowlisting. An endpoint that can trigger an email send must not be callable
		/// by anyone who finds the URL, so the token stands in for a signature.
		///
		/// Nullable rather than required because the webhook is opt-in: a host that never calls
		/// <c>AddEmailSwitchWebhookEndpoints()</c> has no reason to configure one. That call is what
		/// fails when it is missing.
		/// </summary>
		internal readonly string? WebhookToken;

		public BrevoInitializer(
			EmailSwitchGeneralInitializer emailSwitchGeneralInitializer,
			IHttpClientFactory httpClientFactory,
			ILogger<BrevoInitializer> logger)
		{
			_httpClientFactory = httpClientFactory;

			var brevoConfig = emailSwitchGeneralInitializer.EmailSwitchSettings.GetSection(EmailProvider.Brevo.ToString());

			var from = brevoConfig["From"];
			var apiKey = brevoConfig["ApiKey"];

			// Named and thrown rather than logged and swallowed. A swallowed failure here leaves the
			// settings null and turns every later send into a caught NullReferenceException, so email
			// silently never goes out behind a single startup log line.
			var settingsPath = $"{ConstantStrings.EmailSwitchSettingsName}:{EmailProvider.Brevo}";

			if (string.IsNullOrWhiteSpace(from))
			{
				throw new ArgumentException($"{settingsPath}:From is missing. Brevo cannot send without a sender address.", nameof(emailSwitchGeneralInitializer));
			}

			if (string.IsNullOrWhiteSpace(apiKey))
			{
				throw new ArgumentException($"{settingsPath}:ApiKey (the Brevo API key) is missing.", nameof(emailSwitchGeneralInitializer));
			}

			BrevoSettings = new BrevoSettings()
			{
				OtpLength = emailSwitchGeneralInitializer.EmailSwitchGeneralSettings.OtpLength,
				BrevoPrivateSettings = new BrevoPrivateSettings()
				{
					From = from,
					ApiKey = apiKey
				}
			};
			ApiKey = apiKey;

			var webhookToken = brevoConfig["WebhookToken"];
			WebhookToken = string.IsNullOrWhiteSpace(webhookToken) ? null : webhookToken;

			logger.LogInformation("Brevo initialised for sender {From}.", from);
		}

		internal HttpClient CreateClient() => _httpClientFactory.CreateClient(HttpClientName);
	}
}
