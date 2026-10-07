using Beam.App.Infrastructure;
using Beam.Core;
using Beam.Core.Diagnostics;
using Beam.Core.Discovery;
using Beam.Core.Transfer;

namespace Beam.App.ViewModels.Dialogs;

/// <summary>Fallback when automatic discovery can't see a computer: connect using the address it shows.</summary>
public sealed class AddDeviceViewModel : DialogViewModel
{
    private readonly BeamNode _node;
    private string _address = "";
    private string _error = "";
    private bool _isBusy;
    private CancellationTokenSource? _cts;

    public AddDeviceViewModel(BeamNode node)
    {
        _node = node;
        var addresses = node.GetLocalAddresses();
        LocalAddresses = addresses.Count == 0 ? "Not connected to a network" : string.Join("   or   ", addresses);
        ConnectCommand = new AsyncCommand(ConnectAsync, () => !IsBusy && Address.Trim().Length > 0);
        CancelCommand = new RelayCommand(() =>
        {
            _cts?.Cancel();
            Close(null);
        });
    }

    public string LocalAddresses { get; }

    public string Address
    {
        get => _address;
        set
        {
            if (SetProperty(ref _address, value))
            {
                Error = "";
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => Error.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) ConnectCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncCommand ConnectCommand { get; }

    public RelayCommand CancelCommand { get; }

    public override bool CanDismiss => !IsBusy;

    private async Task ConnectAsync()
    {
        IsBusy = true;
        Error = "";
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            DeviceInfo device = await _node.AddDeviceByAddressAsync(Address, _cts.Token);
            Close(device);
        }
        catch (TransferException ex)
        {
            Error = ex.Error.Message;
            Log.Info($"Manual connect to '{Address}' failed: {ex.Error.Details ?? ex.Error.Message}");
        }
        catch (OperationCanceledException)
        {
            Error = "Connecting took too long. Check the address and that Beam is open on the other computer.";
        }
        catch (Exception ex)
        {
            Error = "Couldn't connect to that address.";
            Log.Warn($"Manual connect to '{Address}' failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
