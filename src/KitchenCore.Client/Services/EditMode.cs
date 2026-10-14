namespace KitchenCore.Client.Services;

/// <summary>
/// Whether the menu views are being edited or just read.
///
/// Viewing is the default, even for an editor: most of the time the week is
/// open to see what is for dinner, and a grid where every tap opens an editor
/// and every brush of a finger starts a drag is a poor thing to read from.
/// Editing is switched on deliberately and stays on while moving between the
/// menu views, so planning across week, multi-week and list does not mean
/// re-enabling it on each one. It is not persisted: a reload is back to reading.
/// </summary>
public sealed class EditMode
{
    public bool On { get; private set; }

    /// <summary>Raised when the mode flips, so every view showing it redraws.</summary>
    public event Action? Changed;

    public void Toggle() => Set(!On);

    public void Set(bool on)
    {
        if (On == on)
        {
            return;
        }

        On = on;
        Changed?.Invoke();
    }
}
