using Hoard.Desktop.Navigation;
using Hoard.Desktop.ViewModels;
using Xunit;

namespace Hoard.Desktop.Tests;

public class NavigationServiceTests
{
    // A throwaway concrete page (ViewModelBase is abstract).
    private sealed class Page : ViewModelBase { }

    [Fact]
    public void Reset_sets_the_root_and_cannot_go_back()
    {
        var nav = new NavigationService();
        var root = new Page();

        nav.Reset(root);

        Assert.Same(root, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Push_shows_the_new_page_and_enables_back()
    {
        var nav = new NavigationService();
        var root = new Page();
        var next = new Page();

        nav.Reset(root);
        nav.Push(next);

        Assert.Same(next, nav.Current);
        Assert.True(nav.CanGoBack);
    }

    [Fact]
    public void Pop_returns_to_the_previous_page()
    {
        var nav = new NavigationService();
        var root = new Page();
        var next = new Page();

        nav.Reset(root);
        nav.Push(next);
        nav.Pop();

        Assert.Same(root, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Pop_at_the_root_is_a_no_op()
    {
        var nav = new NavigationService();
        var root = new Page();

        nav.Reset(root);
        nav.Pop();

        Assert.Same(root, nav.Current);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void Reset_clears_a_deep_stack()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());
        nav.Push(new Page());
        nav.Push(new Page());

        var fresh = new Page();
        nav.Reset(fresh);

        Assert.Same(fresh, nav.Current);
        Assert.False(nav.CanGoBack); // the old pages are gone, not buried beneath
    }

    [Fact]
    public void CanGoBack_notifies_when_navigation_changes()
    {
        var nav = new NavigationService();
        nav.Reset(new Page());

        var raised = 0;
        nav.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NavigationService.CanGoBack)) raised++; };

        nav.Push(new Page()); // false → true
        nav.Pop();            // true → false

        Assert.Equal(2, raised);
    }
}
