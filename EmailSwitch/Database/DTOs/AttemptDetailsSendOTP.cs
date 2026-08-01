using EmailSwitch.Common;
using MongoDB.Bson.Serialization.Attributes;

namespace EmailSwitch.Database.DTOs
{
	public record AttemptDetailsSendOTP(
		[property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)] DateTime AttemptTimeInUTC,
		EmailProvider EmailProvider,
		bool SentSuccessfully);
}
