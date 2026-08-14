using EmailSwitch.Services.Brevo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace EmailSwitch.Webhooks.Brevo
{
	public static class BrevoWebhookEndpoint
	{
		public const string BrevoWebhookRoute = "/webhooks/brevo/";

		/// <summary>
		/// The URL to configure in Brevo, given the public root of your API.
		/// </summary>
		public static string BrevoWebhookRelativeUrl(string webhookToken) =>
			$"{Common.ConstantStrings.EmailSwitchGroupName}{BrevoWebhookRoute}{webhookToken}";

		/// <summary>
		/// Always answers 200, whatever it decided. A webhook receiver that reports failure gets the
		/// event redelivered, and none of the outcomes here are retryable: an unrecognised payload, an
		/// event for a session that has since been verified, or a budget already spent are all final.
		/// Answering 4xx would earn a redelivery storm for events that will never be actionable.
		///
		/// The one thing that does answer 404 is a bad token, and only so the endpoint is
		/// indistinguishable from one that is not mapped.
		/// </summary>
		public static RouteGroupBuilder GroupBrevoWebhookApisV1(this RouteGroupBuilder group)
		{
			group.MapPost(BrevoWebhookRoute + "{token}", async (
				string token,
				HttpRequest request,
				BrevoInitializer brevoInitializer,
				DeliveryFailoverService deliveryFailoverService,
				ILogger<RouteGroupBuilder> logger) =>
			{
				if (!TokenMatches(brevoInitializer.WebhookToken, token))
				{
					// Not logged with the supplied token: it is attacker-controlled and would be written
					// verbatim into the log. That the endpoint was called with a wrong one is the whole
					// signal.
					logger.LogWarning("A Brevo webhook call presented an incorrect token and was rejected.");
					return Results.NotFound();
				}

				try
				{
					// Read as a raw string rather than bound to a model: Brevo sends a different field
					// set per event, and an unrecognised payload has to be logged rather than 400'd.
					using var reader = new StreamReader(request.Body, Encoding.UTF8);
					var body = await reader.ReadToEndAsync();

					var deliveryEvent = BrevoDeliveryEventParser.Parse(body);

					if (deliveryEvent is null)
					{
						logger.LogWarning("A Brevo webhook payload could not be understood and was ignored.");
						return Results.Ok();
					}

					await deliveryFailoverService.Handle(deliveryEvent);
				}
				catch (Exception exception)
				{
					// Contained rather than surfaced. A 5xx earns a redelivery, and if the failure is in
					// our own handling then redelivering it will fail the same way.
					logger.LogCritical(exception, "Unable to handle a Brevo delivery webhook.");
				}

				return Results.Ok();
			})
			.Produces(StatusCodes.Status200OK)
			.Produces(StatusCodes.Status404NotFound);

			return group;
		}

		/// <summary>
		/// Fixed-time, so the endpoint does not leak the configured token one character at a time to
		/// anyone willing to measure the response.
		/// </summary>
		private static bool TokenMatches(string? configuredToken, string suppliedToken)
		{
			if (string.IsNullOrWhiteSpace(configuredToken))
			{
				return false;
			}

			return CryptographicOperations.FixedTimeEquals(
				Encoding.UTF8.GetBytes(configuredToken),
				Encoding.UTF8.GetBytes(suppliedToken));
		}
	}
}
