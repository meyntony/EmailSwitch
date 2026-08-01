using System.Net.Mail;

namespace EmailSwitch.Common.DTOs
{
	public struct EmailIdentifier
	{
		private readonly string idValue;
		private readonly string rawValue;
		private readonly MailAddress mailAddress;

		public EmailIdentifier(string email)
		{

			if (string.IsNullOrWhiteSpace(email)) { throw new Exception($"Invalid Email>>{email}<<"); }
			mailAddress = new MailAddress(email.ToLowerInvariant());
			rawValue = email;
			mailAddress = new MailAddress($"{mailAddress.User.Split('+').First()}@{mailAddress.Host}");
			idValue = mailAddress.Host == "gmail.com" ? $"{string.Join("", mailAddress.User.Split('.'))}@{mailAddress.Host}" : mailAddress.ToString();
		}
		public EmailIdentifier(string email, string emailId) : this(email)
		{
			if (emailId.ToLowerInvariant() != idValue) { throw new Exception($"Email ID mismatch>>{emailId} != {idValue}<<"); }
		}

		public override bool Equals(object? obj)
		{
			if (obj is EmailIdentifier tokenIdentifier)
			{
				return Equals(tokenIdentifier);
			}

			return false;
		}
		public override int GetHashCode()
		{
			return idValue.GetHashCode();
		}
		public bool Equals(EmailIdentifier other)
		{
			return !string.IsNullOrEmpty(idValue) && idValue == other.idValue;
		}

		public override string ToString()
		{
			return idValue;
		}
		public string GetRawValue()
		{
			return rawValue;
		}
		public MailAddress GetMailAddress()
		{
			return mailAddress;
		}

		public static implicit operator EmailIdentifier(string value)
		{
			return new EmailIdentifier(value);
		}

		public static explicit operator string(EmailIdentifier tokenIdentifier)
		{
			return tokenIdentifier.idValue;
		}


		public static bool operator ==(EmailIdentifier left, EmailIdentifier right)
		{
			return left.Equals(right);
		}

		public static bool operator !=(EmailIdentifier left, EmailIdentifier right)
		{
			return !left.Equals(right);
		}
	}
}
