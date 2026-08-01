namespace EmailSwitch.EmailTemplates.DTOs
{
	public sealed class EmailContent
	{
		public required string Subject { get; init; }
		public required string PlainTextContent { get; init; }
		public required string HtmlContent { get; init; }
	}
}
