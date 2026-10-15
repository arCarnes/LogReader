namespace LogReader.Tests;

public class InteractiveDesktopTests
{
    [Theory]
    [InlineData(false, 1, true)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    public void UnavailableDesktop_FailsWithInstructionsRatherThanSkipping(
        bool interactive, int session, bool receivesInput)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            InteractiveDesktop.Validate(interactive, session, receivesInput));
        Assert.Contains("not a service", error.Message);
        Assert.Contains("--filter Category!=InteractiveDesktop", error.Message);
    }

    [Fact]
    public void AvailableInputDesktop_PassesPreflight()
        => InteractiveDesktop.Validate(true, 1, true);
}
