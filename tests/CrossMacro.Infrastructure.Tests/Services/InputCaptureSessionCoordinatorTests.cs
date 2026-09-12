namespace CrossMacro.Infrastructure.Tests.Services;

public sealed class InputCaptureSessionCoordinatorTests
{
    [Fact]
    public async Task TwoSessions_WithCompatibleConfigs_ShareOnePhysicalCapture()
    {
        var physical = Substitute.For<IInputCapture>();
        _ = physical.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var factoryCalls = 0;
        InputCaptureSessionFactory factory = () =>
        {
            factoryCalls++;
            return physical;
        };
        using var coordinator = new InputCaptureSessionCoordinator(factory);

        var sessionA = coordinator.CreateSession();
        sessionA.Configure(captureMouse: true, captureKeyboard: true);
        await sessionA.StartAsync(CancellationToken.None);

        var sessionB = coordinator.CreateSession();
        sessionB.Configure(captureMouse: false, captureKeyboard: true);
        await sessionB.StartAsync(CancellationToken.None);

        _ = factoryCalls.Should().Be(1);
        physical.Received(1).Configure(captureMouse: true, captureKeyboard: true);
    }

    [Fact]
    public async Task Events_FanOutToAllActiveSessions_WithSessionAsSender()
    {
        var physical = Substitute.For<IInputCapture>();
        _ = physical.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        using var coordinator = new InputCaptureSessionCoordinator(() => physical);

        var sessionA = coordinator.CreateSession();
        var sessionB = coordinator.CreateSession();
        object? senderA = null;
        object? senderB = null;
        sessionA.InputReceived += (s, _) => senderA = s;
        sessionB.InputReceived += (s, _) => senderB = s;

        sessionA.Configure(true, true);
        await sessionA.StartAsync(CancellationToken.None);
        sessionB.Configure(false, true);
        await sessionB.StartAsync(CancellationToken.None);

        physical.InputReceived += Raise.Event<EventHandler<CapturedInputEventArgs>>(
            physical, new CapturedInputEventArgs());

        _ = senderA.Should().BeSameAs(sessionA);
        _ = senderB.Should().BeSameAs(sessionB);
    }

    [Fact]
    public async Task StoppedSession_DoesNotReceiveEvents()
    {
        var physical = Substitute.For<IInputCapture>();
        _ = physical.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        using var coordinator = new InputCaptureSessionCoordinator(() => physical);

        var session = coordinator.CreateSession();
        var received = 0;
        session.InputReceived += (_, _) => received++;
        session.Configure(true, true);
        await session.StartAsync(CancellationToken.None);
        session.StopCapture();

        _ = SpinWait.SpinUntil(() => false, 50); // allow background convergence
        physical.InputReceived += Raise.Event<EventHandler<CapturedInputEventArgs>>(
            physical, new CapturedInputEventArgs());

        _ = received.Should().Be(0);
    }

    [Fact]
    public async Task LastSessionStop_StopsAndDisposesPhysicalCapture()
    {
        var physical = Substitute.For<IInputCapture>();
        _ = physical.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        using var coordinator = new InputCaptureSessionCoordinator(() => physical);

        var session = coordinator.CreateSession();
        session.Configure(true, true);
        await session.StartAsync(CancellationToken.None);

        session.Dispose();

        _ = SpinWait.SpinUntil(
            () => physical.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IInputCapture.StopCapture)),
            TimeSpan.FromSeconds(2));
        physical.Received(1).StopCapture();
        physical.Received(1).Dispose();
    }

    [Fact]
    public async Task CoordinateModeSession_AppliesModeToPhysicalCapture()
    {
        var physical = Substitute.For<IInputCapture, IMouseCoordinateModeInputCapture>();
        _ = physical.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        using var coordinator = new InputCaptureSessionCoordinator(() => physical);

        var session = coordinator.CreateSession();
        var modeCapture = (IMouseCoordinateModeInputCapture)session;
        modeCapture.ConfigureCoordinateMode(useAbsoluteCoordinates: true, useLogicalCoordinates: true);
        session.Configure(true, true);
        await session.StartAsync(CancellationToken.None);

        var modePhysical = (IMouseCoordinateModeInputCapture)physical;
        modePhysical.Received(1).ConfigureCoordinateMode(useAbsoluteCoordinates: true, useLogicalCoordinates: true);
    }

    [Fact]
    public void ConfigureCoordinateMode_WhenAnotherSessionOwnsMode_Throws()
    {
        var physical = Substitute.For<IInputCapture>();
        using var coordinator = new InputCaptureSessionCoordinator(() => physical);

        var first = (IMouseCoordinateModeInputCapture)coordinator.CreateSession();
        first.ConfigureCoordinateMode(true, false);

        var second = (IMouseCoordinateModeInputCapture)coordinator.CreateSession();
        var act = () => second.ConfigureCoordinateMode(false, true);

        _ = act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task PhysicalError_FansOutToActiveSessions_AndRecreatesBackend()
    {
        var first = Substitute.For<IInputCapture>();
        _ = first.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var second = Substitute.For<IInputCapture>();
        _ = second.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var callCount = 0;
        InputCaptureSessionFactory factory = () => ++callCount is 1 ? first : second;

        using var coordinator = new InputCaptureSessionCoordinator(factory);
        var session = coordinator.CreateSession();
        InputCaptureErrorEventArgs? observed = null;
        session.CaptureError += (_, e) => observed = e;
        session.Configure(true, true);
        await session.StartAsync(CancellationToken.None);

        first.CaptureError += Raise.Event<EventHandler<InputCaptureErrorEventArgs>>(
            first, new InputCaptureErrorEventArgs("simulated backend death"));

        _ = observed.Should().NotBeNull();
        _ = observed!.Message.Should().Be("simulated backend death");
        _ = SpinWait.SpinUntil(() => callCount >= 2, TimeSpan.FromSeconds(2));
        _ = SpinWait.SpinUntil(
            () => second.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IInputCapture.Configure)),
            TimeSpan.FromSeconds(2));
        second.Received(1).Configure(captureMouse: true, captureKeyboard: true);
    }

    [Fact]
    public async Task PhysicalStartFault_PropagatesToSession_AndRollsBack()
    {
        var physical = Substitute.For<IInputCapture>();
        _ = physical.StartAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("hook install failed")));
        using var coordinator = new InputCaptureSessionCoordinator(() => physical);

        var session = coordinator.CreateSession();
        session.Configure(true, true);

        var act = async () => await session.StartAsync(CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
