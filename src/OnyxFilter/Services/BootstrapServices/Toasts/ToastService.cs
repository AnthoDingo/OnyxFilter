namespace OnyxFilter.Services.BootstrapServices.Toasts;

public sealed class ToastService
{
    public event EventHandler<ToastInfos>? ToastRequested;

    public void Show(string message, ToastSeverity severity)
    {
        ToastRequested?.Invoke(
            this,
            new ToastInfos(
                Guid.NewGuid(),
                message,
                severity
            )
        );
    }
}
