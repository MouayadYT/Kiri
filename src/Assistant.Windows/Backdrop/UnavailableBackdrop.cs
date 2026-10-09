namespace Assistant.Windows.Backdrop;

/// <summary>Stands in when blur is unavailable. It is never blurred, so the window draws its surfaces opaque.</summary>
internal sealed class UnavailableBackdrop : IWindowBackdrop
{
    public nint Handle => 0;

    public bool IsBlurred => false;

    public event EventHandler? IsBlurredChanged
    {
        add { }
        remove { }
    }

    public void SetRegion(BackdropRegion region)
    {
    }

    public void SetOpacity(double opacity)
    {
    }

    public void Dispose()
    {
    }
}
