using Beam.App.Infrastructure;
using Beam.Core.Localization;

namespace Beam.App.ViewModels;

/// <summary>The parts of Settings, each a short page of its own.</summary>
public enum SettingsSection
{
    /// <summary>Phones: the list of sections (the desktop always shows one).</summary>
    None,
    General,
    Receiving,
    Sharing,
    Backup,
    Connection,
    About,
}

// Settings is split into short sections: a list beside the open one on the desktop; on phones the list, then the
// section (Back returns to the list).
public sealed partial class SettingsViewModel
{
    private SettingsSection _section = SettingsSection.General;
    private bool _sectionsInitialized;

    public SettingsSection Section
    {
        get
        {
            if (!_sectionsInitialized)
            {
                _sectionsInitialized = true;
                if (_platform.IsPhone) _section = SettingsSection.None;
            }

            return _section;
        }
        set
        {
            _ = Section;
            // Backup isn't offered everywhere (iPhones): its section can't be opened there.
            if (value == SettingsSection.Backup && !Backup.IsAvailable) value = _platform.IsPhone ? SettingsSection.None : SettingsSection.General;
            if (_section == value) return;
            if (_section == SettingsSection.General) SaveName();
            _section = value;
            foreach (var name in new[]
                     {
                         nameof(Section), nameof(ShowsList), nameof(ShowsSection), nameof(SectionTitle), nameof(IsGeneral), nameof(IsReceiving),
                         nameof(IsSharing), nameof(IsBackup), nameof(IsConnection), nameof(IsAbout),
                     })
                OnPropertyChanged(name);
        }
    }

    /// <summary>The list of sections is shown (always on the desktop; on phones when none is open).</summary>
    public bool ShowsList => !_platform.IsPhone || Section == SettingsSection.None;

    public bool ShowsSection => Section != SettingsSection.None;

    /// <summary>Phones: a section is open, so its page shows Back to the list.</summary>
    public bool ShowsBack => _platform.IsPhone;

    public string SectionTitle => Section switch
    {
        SettingsSection.General => L.T("General"),
        SettingsSection.Receiving => L.T("Receiving"),
        SettingsSection.Sharing => L.T("Shared folders"),
        SettingsSection.Backup => L.T("Backup"),
        SettingsSection.Connection => L.T("Connection"),
        SettingsSection.About => L.T("About"),
        _ => L.T("Settings"),
    };

    public bool IsGeneral
    {
        get => Section == SettingsSection.General;
        set { if (value) Section = SettingsSection.General; }
    }

    public bool IsReceiving
    {
        get => Section == SettingsSection.Receiving;
        set { if (value) Section = SettingsSection.Receiving; }
    }

    public bool IsSharing
    {
        get => Section == SettingsSection.Sharing;
        set { if (value) Section = SettingsSection.Sharing; }
    }

    public bool IsBackup
    {
        get => Section == SettingsSection.Backup;
        set { if (value) Section = SettingsSection.Backup; }
    }

    public bool IsConnection
    {
        get => Section == SettingsSection.Connection;
        set { if (value) Section = SettingsSection.Connection; }
    }

    public bool IsAbout
    {
        get => Section == SettingsSection.About;
        set { if (value) Section = SettingsSection.About; }
    }

    /// <summary>Connection: Send directly (the same choice as Send's; it shows a note there when on).</summary>
    public bool PreferDirect
    {
        get => _main.Home.PreferDirect;
        set
        {
            _main.Home.PreferDirect = value;
            OnPropertyChanged();
        }
    }

    public bool CanConnectDirectly => _main.Home.CanConnectDirectly;

    public RelayCommand BackToListCommand => _backToList ??= new RelayCommand(() => Section = SettingsSection.None);

    private RelayCommand? _backToList;

    /// <summary>The phone's Back button: a section open → back to the list. False otherwise.</summary>
    public bool GoBack()
    {
        if (!_platform.IsPhone || Section == SettingsSection.None) return false;
        Section = SettingsSection.None;
        return true;
    }
}
