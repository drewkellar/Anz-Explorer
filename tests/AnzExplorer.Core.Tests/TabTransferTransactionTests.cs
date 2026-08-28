using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class TabTransferTransactionTests
{
    [Fact]
    public void Execute_MovesEveryTabInOrder()
    {
        var moved = new List<string>();
        var steps = new[] { new TabTransferStep<string>("A", 0), new("B", 1) };

        var succeeded = TabTransferTransaction.Execute(
            steps,
            step => { moved.Add(step.Tab); return true; },
            step => moved.Remove(step.Tab));

        Assert.True(succeeded);
        Assert.Equal(["A", "B"], moved);
    }

    [Fact]
    public void Execute_RollsBackCompletedTabsInReverseOrderAfterFailure()
    {
        var events = new List<string>();
        var steps = new[] { new TabTransferStep<string>("A", 0), new("B", 1), new("C", 2) };

        var succeeded = TabTransferTransaction.Execute(
            steps,
            step => { events.Add($"move:{step.Tab}"); return step.Tab != "C"; },
            step => events.Add($"rollback:{step.Tab}:{step.OriginalIndex}"));

        Assert.False(succeeded);
        Assert.Equal(["move:A", "move:B", "move:C", "rollback:B:1", "rollback:A:0"], events);
    }

    [Fact]
    public void Execute_RollsBackAfterUnexpectedMoveException()
    {
        var restored = new List<string>();
        var steps = new[] { new TabTransferStep<string>("A", 0), new("B", 1) };

        var succeeded = TabTransferTransaction.Execute(
            steps,
            step => step.Tab == "A" ? true : throw new InvalidOperationException(),
            step => restored.Add(step.Tab));

        Assert.False(succeeded);
        Assert.Equal(["A"], restored);
    }

    [Theory]
    [InlineData(-3, 4, 0)]
    [InlineData(2, 4, 2)]
    [InlineData(99, 4, 4)]
    [InlineData(1, 0, 0)]
    public void ClampDropIndex_AlwaysReturnsAValidInsertionPoint(int requested, int count, int expected) =>
        Assert.Equal(expected, TabTransferTransaction.ClampDropIndex(requested, count));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(12, false)]
    public void ShouldCloseSource_OnlyClosesAnEmptyWindow(int count, bool expected) =>
        Assert.Equal(expected, TabTransferTransaction.ShouldCloseSource(count));
}
