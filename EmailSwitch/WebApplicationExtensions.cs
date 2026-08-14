using EmailSwitch.Common;
using EmailSwitch.Common.Logo;
using EmailSwitch.Services.Brevo;
using EmailSwitch.Webhooks.Brevo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSwitch
{
	public static class WebApplicationExtensions
	{
		public static WebApplication AddEmailSwitchApiEndpoints(this WebApplication app)
		{

			app.MapGroup(ConstantStrings.EmailSwitchGroupName)
				.GroupEmailSignatureLogoApisV1()
				.WithTags(ConstantStrings.EmailSwitchTagName);

			return app;
		}

		/// <summary>
		/// Maps the provider delivery webhooks, which turn a bounce or a block into a resend through the
		/// next provider in the session's budget. See <c>Webhooks/DeliveryFailoverService</c> for why
		/// this cannot be done at send time.
		///
		/// Separate from <see cref="AddEmailSwitchApiEndpoints"/> and opt-in, so upgrading does not give
		/// an existing host a public POST endpoint it never asked for.
		///
		/// Fails fast when the provider is configured without a webhook token. Brevo does not sign its
		/// webhooks, so the token is the only thing standing between this endpoint and anyone who can
		/// guess the URL - and a silently unprotected send trigger is worse than a startup failure.
		/// </summary>
		public static WebApplication AddEmailSwitchWebhookEndpoints(this WebApplication app)
		{
			// Resolved rather than injected at registration, so a host that never calls this still
			// starts without Brevo credentials.
			using var scope = app.Services.CreateScope();
			var brevoInitializer = scope.ServiceProvider.GetRequiredService<BrevoInitializer>();

			if (string.IsNullOrWhiteSpace(brevoInitializer.WebhookToken))
			{
				throw new InvalidOperationException(
					$"{ConstantStrings.EmailSwitchSettingsName}:Brevo:WebhookToken is missing, and {nameof(AddEmailSwitchWebhookEndpoints)} cannot map an unauthenticated webhook that is able to send email. Configure a high-entropy token, or do not call this method.");
			}

			app.MapGroup(ConstantStrings.EmailSwitchGroupName)
				.GroupBrevoWebhookApisV1()
				.WithTags(ConstantStrings.EmailSwitchTagName);

			return app;
		}
	}
}
