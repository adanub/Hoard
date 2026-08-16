using Avalonia;
using Avalonia.Markup.Xaml;

namespace Hoard.Harness;

public partial class HarnessApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
