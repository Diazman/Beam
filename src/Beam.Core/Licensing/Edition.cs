namespace Beam.Core.Licensing;

/// <summary>
/// Capabilities that a future paid edition could gate. Everything here is free today;
/// UI code asks <see cref="IEditionPolicy"/> instead of hard-coding availability, so adding
/// a Pro edition later only means adding a policy implementation and a license source.
/// </summary>
public enum Feature
{
    SendFiles,
    ReceiveFiles,
    SendFolders,
    ResumeTransfers,
    TrustedDevices,
    MultipleSimultaneousTransfers,
    ManualConnection,
}

public interface IEditionPolicy
{
    string EditionName { get; }

    bool IsEnabled(Feature feature);
}

/// <summary>The free edition: every current feature is available.</summary>
public sealed class FreeEdition : IEditionPolicy
{
    public string EditionName => "Free";

    public bool IsEnabled(Feature feature) => true;
}
