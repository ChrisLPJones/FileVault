using System.Net;
using System.Net.Mail;
using System.Threading.Channels;

namespace Backend.Services;

public record EmailMessage(string To, string Subject, string Body);

// Sends an email (plain text)
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

// Sends through the SMTP server in Email:Smtp (Host, Port, User, Password, From, EnableSsl)
public class SmtpEmailSender(IConfiguration config) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var smtp = config.GetSection("Email:Smtp");
        var from = new[] { smtp["From"], smtp["User"] }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            ?? throw new InvalidOperationException("Email:Smtp:From is not set.");

        using var client = new SmtpClient(smtp["Host"], smtp.GetValue("Port", 587))
        {
            EnableSsl = smtp.GetValue("EnableSsl", true),
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrEmpty(smtp["User"]))
            client.Credentials = new NetworkCredential(smtp["User"], smtp["Password"]);

        using var mail = new MailMessage(from, message.To, message.Subject, message.Body);
        await client.SendMailAsync(mail, ct);
    }
}

// Used when no SMTP server is configured (local development): writes the email, including its
// link, to the log instead of sending it.
public class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Email:Smtp is not configured, so this email was not sent.\nTo: {To}\nSubject: {Subject}\n\n{Body}",
            message.To, message.Subject, message.Body);
        return Task.CompletedTask;
    }
}

// Emails are queued and sent in the background, so requests don't wait for the mail server and
// response times don't reveal whether an address has an account (forgot password).
public class EmailQueue
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enqueue(EmailMessage message) => _channel.Writer.TryWrite(message);

    public IAsyncEnumerable<EmailMessage> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public class EmailBackgroundService(EmailQueue queue, IEmailSender sender, ILogger<EmailBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await sender.SendAsync(message, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Sending email \"{Subject}\" failed", message.Subject);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }
}

public static class EmailServiceCollectionExtensions
{
    // SMTP when Email:Smtp:Host is set, otherwise the logging sender
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config["Email:Smtp:Host"]))
            services.AddSingleton<IEmailSender, LogEmailSender>();
        else
            services.AddSingleton<IEmailSender, SmtpEmailSender>();

        services.AddSingleton<EmailQueue>();
        services.AddHostedService<EmailBackgroundService>();
        services.AddScoped<AccountEmailService>();
        return services;
    }
}
