
namespace CrossMacro.Platform.Abstractions;

public interface IInputSimulator : IDisposable
{
    public string ProviderName { get; }

    public bool IsSupported { get; }

    public void Initialize(int screenWidth = 0, int screenHeight = 0);

    public Task InitializeAsync(
        int screenWidth = 0,
        int screenHeight = 0,
        CancellationToken cancellationToken = default);

    public void MoveAbsolute(int x, int y);

    public void MoveRelative(int dx, int dy);

    public void MouseButton(int button, bool pressed);

    /// <summary>
    /// Presses and releases a mouse button as one logical action. The default
    /// implementation issues two separate injections; platform simulators that
    /// support atomic batched injection should override this to submit both
    /// transitions in a single native call.
    /// </summary>
    public void MouseButtonClick(int button)
    {
        MouseButton(button, pressed: true);
        MouseButton(button, pressed: false);
    }

    public void Scroll(int delta, bool isHorizontal = false);

    public void KeyPress(int keyCode, bool pressed);

    public void Sync();
}
