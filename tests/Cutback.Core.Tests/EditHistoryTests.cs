namespace Cutback.Core.Tests;

public sealed class EditHistoryTests
{
    // ---- construction -------------------------------------------------------------------------

    [Fact]
    public void New_history_has_nothing_to_undo_or_redo()
    {
        var history = new EditHistory<string>(10);

        history.CanUndo.Should().BeFalse();
        history.CanRedo.Should().BeFalse();
        history.Limit.Should().Be(10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Limit_below_one_is_clamped_to_one(int limit)
    {
        var history = new EditHistory<string>(limit);

        history.Limit.Should().Be(1);
    }

    // ---- undo / redo --------------------------------------------------------------------------

    [Fact]
    public void Undo_returns_the_state_pushed_before_the_edit()
    {
        var history = new EditHistory<string>(10);

        history.Push("before");
        var restored = history.Undo("after");

        restored.Should().Be("before");
        history.CanUndo.Should().BeFalse();
        history.CanRedo.Should().BeTrue();
    }

    [Fact]
    public void Redo_returns_the_state_that_undo_replaced()
    {
        var history = new EditHistory<string>(10);
        history.Push("before");
        history.Undo("after");

        var redone = history.Redo("before");

        redone.Should().Be("after");
        history.CanUndo.Should().BeTrue();
        history.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Undo_walks_back_through_edits_in_reverse_order()
    {
        var history = new EditHistory<string>(10);
        history.Push("v0");
        history.Push("v1");
        history.Push("v2");

        history.Undo("v3").Should().Be("v2");
        history.Undo("v2").Should().Be("v1");
        history.Undo("v1").Should().Be("v0");
        history.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Push_after_undo_discards_the_redo_stack()
    {
        var history = new EditHistory<string>(10);
        history.Push("v0");
        history.Undo("v1");

        history.Push("v0");

        history.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Undo_with_nothing_to_undo_throws()
    {
        var history = new EditHistory<string>(10);

        var act = () => history.Undo("current");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Redo_with_nothing_to_redo_throws()
    {
        var history = new EditHistory<string>(10);
        history.Push("v0");

        var act = () => history.Redo("current");

        act.Should().Throw<InvalidOperationException>();
    }

    // ---- limit --------------------------------------------------------------------------------

    [Fact]
    public void Pushing_beyond_the_limit_drops_the_oldest_entry()
    {
        var history = new EditHistory<string>(2);
        history.Push("v0");
        history.Push("v1");
        history.Push("v2");

        history.Undo("v3").Should().Be("v2");
        history.Undo("v2").Should().Be("v1");
        history.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Lowering_the_limit_trims_the_oldest_entries_immediately()
    {
        var history = new EditHistory<string>(10);
        history.Push("v0");
        history.Push("v1");
        history.Push("v2");

        history.Limit = 1;

        history.Undo("v3").Should().Be("v2");
        history.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Raising_the_limit_keeps_existing_entries()
    {
        var history = new EditHistory<string>(2);
        history.Push("v0");
        history.Push("v1");

        history.Limit = 5;

        history.Undo("v2").Should().Be("v1");
        history.Undo("v1").Should().Be("v0");
    }

    // ---- clear --------------------------------------------------------------------------------

    [Fact]
    public void Clear_empties_both_stacks()
    {
        var history = new EditHistory<string>(10);
        history.Push("v0");
        history.Push("v1");
        history.Undo("v2");

        history.Clear();

        history.CanUndo.Should().BeFalse();
        history.CanRedo.Should().BeFalse();
    }

    // ---- change notification ------------------------------------------------------------------

    [Fact]
    public void Changed_is_raised_by_push_undo_redo_and_clear()
    {
        var history = new EditHistory<string>(10);
        var count = 0;
        history.Changed += (_, _) => count++;

        history.Push("v0");
        history.Undo("v1");
        history.Redo("v0");
        history.Clear();

        count.Should().Be(4);
    }
}
