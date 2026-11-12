using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class NavigationRevealTests
{
    private static readonly string[] Children = [@"C:\Users\Alice\Documents", @"C:\Users\Alice\Downloads", @"C:\Users\Alice\notes.txt"];

    [Fact]
    public void Back_one_level_reveals_the_folder_that_was_left() =>
        Assert.Equal(@"C:\Users\Alice\Downloads", NavigationReveal.FindItemToReveal(@"C:\Users\Alice\Downloads", Children));

    [Fact]
    public void Back_several_levels_reveals_the_containing_child() =>
        Assert.Equal(@"C:\Users\Alice\Documents", NavigationReveal.FindItemToReveal(@"C:\Users\Alice\Documents\Work\2026", Children));

    [Fact]
    public void Matching_ignores_case_and_trailing_separators() =>
        Assert.Equal(@"C:\Users\Alice\Downloads", NavigationReveal.FindItemToReveal(@"c:\users\alice\DOWNLOADS\", Children));

    [Fact]
    public void Similar_prefix_is_not_treated_as_a_parent() =>
        Assert.Null(NavigationReveal.FindItemToReveal(@"C:\Users\Alice\Documents2", Children));

    [Fact]
    public void Unrelated_previous_location_reveals_nothing() =>
        Assert.Null(NavigationReveal.FindItemToReveal(@"D:\Media", Children));

    [Fact]
    public void Up_to_this_pc_reveals_the_drive() =>
        Assert.Equal(@"C:\", NavigationReveal.FindItemToReveal(@"C:\Users", [@"C:\", @"D:\"]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anz://this-pc")]
    public void Non_file_system_locations_reveal_nothing(string? previous) =>
        Assert.Null(NavigationReveal.FindItemToReveal(previous, Children));
}
