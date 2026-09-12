namespace CrossMacro.Infrastructure.Tests.Services;

public sealed class RunScriptScreenReadingWhitespaceTests
{
    [Theory]
    [MemberData(nameof(MixedWhitespaceCases))]
    public void TryParseCommand_WithMixedUnicodeWhitespace_UsesTheSameTokensAsValidation(string step, string[] expectedParts)
    {
        var parsed = RunScriptScreenReadingStepParser.TryParseCommand(step, out _, out var parts);

        Assert.True(parsed);
        Assert.Equal(expectedParts, parts);
        Assert.True(RunScriptScreenReadingStepParser.TryValidateStep(step, out var error), error);
        Assert.Null(error);
    }

    public static IEnumerable<object[]> MixedWhitespaceCases()
    {
        yield return new object[]
        {
            "pixelcolor\t1 \r\n2\u00A0color",
            new[] { "pixelcolor", "1", "2", "color" },
        };
        yield return new object[]
        {
            "waitcolor\t1 2\r\n00FF00\u00A00\twait_ok",
            new[] { "waitcolor", "1", "2", "00FF00", "0", "wait_ok" },
        };
        yield return new object[]
        {
            "pixelsearch\t0 0\r\n10\u00A010 123456\tfound x y\ttolerance 3",
            new[] { "pixelsearch", "0", "0", "10", "10", "123456", "found", "x", "y", "tolerance", "3" },
        };
        yield return new object[]
        {
            "imagesearch\tTarget\r\nfound x y\u00A0similarity 0.9",
            new[] { "imagesearch", "Target", "found", "x", "y", "similarity", "0.9" },
        };
        yield return new object[]
        {
            "imageclick\tTarget\r\nbutton\u00A0right",
            new[] { "imageclick", "Target", "button", "right" },
        };
        yield return new object[]
        {
            "waitimage\tTarget\r\ntimeout\u00A0100",
            new[] { "waitimage", "Target", "timeout", "100" },
        };
    }

    [Fact]
    public void Compile_WhenScreenCommandHasMixedWhitespaceAndInvalidShape_ReturnsSyntaxError()
    {
        var compiler = new RunScriptCompiler(Substitute.For<IKeyCodeMapper>());

        var result = compiler.Compile([new RunScriptStep("pixelcolor\t1\r\n")]);

        Assert.False(result.Success);
        Assert.Contains("Invalid pixelcolor syntax", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteStepAsync_WhenScreenCommandUsesMixedWhitespace_ReadsTheParsedCoordinates()
    {
        var reader = Substitute.For<IScreenPixelReader>();
        ScreenPoint? requestedPoint = null;
        _ = reader.GetPixelAsync(Arg.Any<ScreenPoint>(), Arg.Any<ScreenReadOptions>()).Returns(callInfo =>
        {
            requestedPoint = callInfo.Arg<ScreenPoint>();
            return Task.FromResult(ScreenReadResultFactory.Success(new ScreenPixelColor(0x12, 0x34, 0x56)));
        });
        var executor = new RunScriptScreenReadExecutor(reader, mousePositionProvider: null);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await executor.ExecuteStepAsync(
            "pixelcolor\t1\r\n2\u00A0sampled",
            stepNumber: 4,
            runtimeVariables: variables,
            cancellationToken: CancellationToken.None);

        Assert.Equal(new ScreenPoint(1, 2), requestedPoint);
        Assert.Equal("123456", variables["sampled"]);
    }

    [Fact]
    public async Task ExecuteStepAsync_WhenRecognizedScreenCommandIsMalformed_ThrowsSyntaxError()
    {
        var executor = new RunScriptScreenReadExecutor(
            Substitute.For<IScreenPixelReader>(),
            mousePositionProvider: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteStepAsync(
            "pixelcolor\t1\r\n",
            stepNumber: 4,
            runtimeVariables: new Dictionary<string, string>(),
            cancellationToken: CancellationToken.None));

        Assert.Contains("Step 4", exception.Message);
        Assert.Contains("Invalid pixelcolor syntax", exception.Message);
    }
}
