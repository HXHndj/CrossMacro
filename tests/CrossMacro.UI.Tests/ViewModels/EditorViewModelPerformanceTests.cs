using Xunit.Abstractions;

namespace CrossMacro.UI.Tests.ViewModels;

public sealed class EditorViewModelPerformanceTests(ITestOutputHelper output)
{
    private const int EditsPerSample = 20;
    private const long MaxBytesPerActionEdit = 384;

    [Fact]
    [Trait("Category", "Performance")]
    public void LargeActionList_SameFieldEdit_ReportsAllocationAndResponseBudget()
    {
        WarmUp();

        foreach (var actionCount in new[] { 1_000, 5_000 })
        {
            using var viewModel = CreateViewModel(actionCount);
            var action = viewModel.Actions[actionCount / 2];
            viewModel.SelectedAction = action;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var start = Stopwatch.GetTimestamp();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var edit = 0; edit < EditsPerSample; edit++)
            {
                action.Text = $"edited-{edit}";
            }

            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            output.WriteLine(
                $"N={actionCount} K={EditsPerSample} elapsed_ms={elapsedMs:F3} allocated_bytes={allocatedBytes}");

            // This is deliberately a budget rather than a machine-specific latency claim.
            // The old per-edit full action clone path scales with N action objects; the
            // optimized path only copies an N-length reference array and one changed action.
            var allocationBudget = checked((long)actionCount * EditsPerSample * MaxBytesPerActionEdit);
            Assert.True(
                allocatedBytes < allocationBudget,
                $"Allocation budget exceeded for N={actionCount}: {allocatedBytes} >= {allocationBudget} bytes.");
            Assert.True(
                elapsedMs < 5_000,
                $"The small response sample exceeded 5 seconds for N={actionCount}: {elapsedMs:F3} ms.");
        }
    }

    private static void WarmUp()
    {
        using var viewModel = CreateViewModel(8);
        var action = viewModel.Actions[4];
        viewModel.SelectedAction = action;
        action.Text = "warmup-1";
        action.Text = "warmup-2";
    }

    private static EditorViewModel CreateViewModel(int actionCount)
    {
        var converter = Substitute.For<IEditorActionConverter>();
        var validator = Substitute.For<IEditorActionValidator>();
        var captureService = Substitute.For<ICoordinateCaptureService>();
        var fileManager = Substitute.For<IMacroFileManager>();
        var dialogService = Substitute.For<IDialogService>();
        var keyCodeMapper = Substitute.For<IKeyCodeMapper>();
        var macroPlayer = Substitute.For<IMacroPlayer>();
        var localizationService = Substitute.For<ILocalizationService>();

        _ = localizationService.CurrentCulture.Returns(CultureInfo.InvariantCulture);
        _ = localizationService[Arg.Any<string>()].Returns(call => call.Arg<string>());
        _ = validator.ValidateAll(Arg.Any<IEnumerable<EditorAction>>()).Returns((true, new List<string>()));

        var actions = Enumerable.Range(0, actionCount)
            .Select(index => new EditorAction
            {
                Type = EditorActionType.TextInput,
                Text = $"row-{index}",
            })
            .ToList();
        var sequence = new MacroSequence { Name = "Performance Sample" };
        _ = converter.FromMacroSequenceWithDiagnostics(sequence)
            .Returns(new EditorActionRestoreResult(actions, [], restoredFromScriptSteps: false));

        var viewModel = new EditorViewModel(
            converter,
            validator,
            captureService,
            fileManager,
            dialogService,
            keyCodeMapper,
            macroPlayer,
            localizationService,
            new EditorActionDisplayFormatter(localizationService));
        viewModel.LoadMacroSequence(sequence);
        return viewModel;
    }
}
