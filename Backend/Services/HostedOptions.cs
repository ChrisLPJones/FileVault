namespace Backend.Services;

// Whether this is the public hosted instance or a self-hosted one (FILEVAULT_MODE -> App:Mode),
// and the hosted-only settings. Self-hosted is the default; any value other than "self-hosted" or
// "hosted" stops the API starting. Inactive-account removal and the first-login notice only exist
// in hosted mode.
public sealed record HostedOptions(bool IsHosted, int InactiveDays, int WarningDays, int MaxRemovalsPerRun, string? ContactEmail)
{
    public const string SelfHostedName = "self-hosted";
    public const string HostedName = "hosted";

    public string ModeName => IsHosted ? HostedName : SelfHostedName;

    public static HostedOptions From(IConfiguration config)
    {
        var raw = config["App:Mode"]?.Trim();
        bool hosted;
        if (string.IsNullOrEmpty(raw) || raw.Equals(SelfHostedName, StringComparison.OrdinalIgnoreCase))
            hosted = false;
        else if (raw.Equals(HostedName, StringComparison.OrdinalIgnoreCase))
            hosted = true;
        else
            throw new InvalidOperationException(
                $"FILEVAULT_MODE (App:Mode) must be \"{SelfHostedName}\" or \"{HostedName}\", but it is \"{raw}\". " +
                "Leave it unset for a self-hosted install.");

        var inactiveDays = Math.Max(1, config.GetValue("Hosted:InactiveDays", 30));
        // The warning goes out WarningDays before the removal, so it must be shorter than the inactivity period
        var warningDays = Math.Min(Math.Max(1, config.GetValue("Hosted:WarningDays", 7)), Math.Max(1, inactiveDays - 1));
        var contact = config["Hosted:ContactEmail"]?.Trim();

        return new HostedOptions(hosted, inactiveDays, warningDays,
            Math.Max(1, config.GetValue("Hosted:MaxRemovalsPerRun", 50)),
            string.IsNullOrEmpty(contact) ? null : contact);
    }
}
