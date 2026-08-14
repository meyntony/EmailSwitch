using EmailSwitch.Common;
using MongoDB.Bson.Serialization.Attributes;

namespace EmailSwitch.Database.DTOs
{
	/// <summary>
	/// <paramref name="ProviderMessageId"/> is appended last and defaulted so existing positional
	/// construction still compiles. It is null on a failed attempt, on providers that report no id, and
	/// on every attempt recorded before the field existed - which is why the delivery-event lookup has
	/// to tolerate its absence rather than assume every attempt carries one.
	/// </summary>
	public record AttemptDetailsSendOTP(
		[property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)] DateTime AttemptTimeInUTC,
		EmailProvider EmailProvider,
		bool SentSuccessfully,
		string? ProviderMessageId = null);
}
