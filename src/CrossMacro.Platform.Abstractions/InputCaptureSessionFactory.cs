
namespace CrossMacro.Platform.Abstractions;

/// <summary>
/// Creates a new physical input capture instance owned by the shared
/// <c>InputCaptureSessionCoordinator</c>. Kept as a distinct delegate type so
/// the DI container can hand consumers a session factory under
/// <c>Func&lt;IInputCapture&gt;</c> while the coordinator still resolves the
/// physical factory without a registration cycle.
/// </summary>
public delegate IInputCapture InputCaptureSessionFactory();
