using EmailSwitch.Common;
using System.Text.Json;

namespace EmailSwitch.Webhooks.Brevo
{
	/// <summary>
	/// Maps a Brevo transactional webhook payload onto a <see cref="DeliveryEvent"/>.
	///
	/// Parsed with JsonDocument rather than bound to a type: Brevo sends a different field set per
	/// event, and a payload this library does not fully recognise must still yield the three fields the
	/// failover decision needs rather than failing to deserialise.
	/// </summary>
	internal static class BrevoDeliveryEventParser
	{
		/// <summary>
		/// Events after which the message is known never to arrive.
		///
		/// <c>spam</c> is deliberately absent: the recipient marked a message they <em>received</em>,
		/// so resending would deliver a second copy of a code that already arrived and annoy someone
		/// who has just said they do not want it. <c>deferred</c> and <c>softBounce</c> are absent
		/// because Brevo retries them itself.
		/// </summary>
		private static readonly HashSet<string> TerminalFailureEvents =
			new(StringComparer.OrdinalIgnoreCase) { "hardBounce", "hard_bounce", "blocked", "invalid", "invalid_email", "error" };

		internal static DeliveryEvent? Parse(string body)
		{
			try
			{
				using var document = JsonDocument.Parse(body);
				var root = document.RootElement;

				if (root.ValueKind != JsonValueKind.Object)
				{
					return null;
				}

				var eventName = ReadString(root, "event");
				var providerMessageId = ReadString(root, "message-id") ?? ReadString(root, "message_id");

				if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(providerMessageId))
				{
					return null;
				}

				return new DeliveryEvent(
					EmailProvider.Brevo,
					providerMessageId,
					eventName,
					TerminalFailureEvents.Contains(eventName),
					ReadString(root, "reason"));
			}
			catch (JsonException)
			{
				return null;
			}
		}

		private static string? ReadString(JsonElement root, string propertyName) =>
			root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;
	}
}
