using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace SocietyHub.Guard.App.Platform;

/// <summary>
/// Scans a gate pass.
///
/// Behind an interface because the scanner is a native MAUI view and the gate screen is Blazor.
/// The seam also means the gate screen can be reasoned about without a camera — which matters,
/// because the camera is the part that cannot be tested in CI and the check-in logic behind it
/// is the part that must never be wrong.
/// </summary>
public interface IBarcodeScanner
{
    /// <summary>
    /// Opens the camera and returns the first pass code read, or null if the guard backed out
    /// or the camera is unavailable.
    ///
    /// Null is an ordinary outcome, not a failure. A guard who cancels the scan is going to
    /// type the code instead, and the typed path must always remain open.
    /// </summary>
    Task<string?> ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The camera scanner, as a modal page over the gate screen.
///
/// Modal rather than embedded, deliberately. A live camera preview sitting permanently on the
/// gate screen holds the camera open all shift, drains a wall-mounted tablet that is often on a
/// marginal charger, and keeps a lens pointed at the gate recording nothing — which is exactly
/// the kind of thing a resident committee will ask about.
/// </summary>
public sealed class MauiBarcodeScanner : IBarcodeScanner
{
    public async Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        var granted = await EnsureCameraPermissionAsync();

        if (!granted)
        {
            // Refused, or the tablet has no camera. Returning null sends the guard back to the
            // keyboard rather than blocking them, which is the whole reason the typed path
            // exists.
            return null;
        }

        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var page = new ScanPage(completion);

        await Application.Current!.Windows[0].Page!.Navigation.PushModalAsync(page);

        // Cancellation closes the sheet rather than abandoning it open — a modal left on screen
        // on a wall-mounted tablet is a gate nobody can use.
        await using var registration = cancellationToken.Register(() => completion.TrySetResult(null));

        var result = await completion.Task;

        await Application.Current.Windows[0].Page!.Navigation.PopModalAsync();

        return result;
    }

    private static async Task<bool> EnsureCameraPermissionAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();

        if (status == PermissionStatus.Granted)
        {
            return true;
        }

        // Asked at the point of use rather than on first launch. A permission prompt during
        // onboarding, before anyone has seen why it is needed, is the one most often refused.
        status = await Permissions.RequestAsync<Permissions.Camera>();

        return status == PermissionStatus.Granted;
    }

    /// <summary>The modal itself. Built in code so it can be handed a completion source.</summary>
    private sealed class ScanPage : ContentPage
    {
        private readonly TaskCompletionSource<string?> _completion;
        private readonly CameraBarcodeReaderView _reader;

        public ScanPage(TaskCompletionSource<string?> completion)
        {
            _completion = completion;

            _reader = new CameraBarcodeReaderView
            {
                Options = new BarcodeReaderOptions
                {
                    // Only the formats a gate pass actually uses. Every additional format is
                    // work done on every frame, and on the low-end tablets societies buy that
                    // is the difference between an instant read and a guard holding a phone
                    // steady for five seconds.
                    Formats = BarcodeFormat.QrCode | BarcodeFormat.Code128,

                    AutoRotate = true,
                    Multiple = false,

                    // A gate is often dim, and a pass is often a phone screen behind glass.
                    TryHarder = true,
                },
            };

            _reader.BarcodesDetected += OnDetected;

            Content = new Grid
            {
                Children =
                {
                    _reader,

                    new Button
                    {
                        Text = "Cancel",
                        VerticalOptions = LayoutOptions.End,
                        HorizontalOptions = LayoutOptions.Center,
                        Margin = new Thickness(0, 0, 0, 32),

                        // Always reachable. A scanner with no way out on a wall-mounted tablet
                        // is a gate that needs a reboot to reopen.
                        Command = new Command(() => _completion.TrySetResult(null)),
                    },
                },
            };
        }

        private void OnDetected(object? sender, BarcodeDetectionEventArgs e)
        {
            var value = e.Results.FirstOrDefault()?.Value;

            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            // Detached immediately. The camera keeps producing frames while the modal is
            // dismissing, and a second detection would resolve a completed task and — worse —
            // could check the same visitor in twice.
            _reader.BarcodesDetected -= OnDetected;
            _reader.IsDetecting = false;

            _completion.TrySetResult(value);
        }

        protected override bool OnBackButtonPressed()
        {
            _completion.TrySetResult(null);
            return base.OnBackButtonPressed();
        }
    }
}
