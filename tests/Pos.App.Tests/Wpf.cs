using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pos.App.Tests;

/// <summary>
/// One STA thread with a dispatcher and an <see cref="Application"/>, shared by every test that
/// needs real WPF layout.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than one per test, and deliberately: a process may hold only one
/// <see cref="Application"/>, and a window belongs to the dispatcher of the thread that made it.
/// Two tests each spinning up their own would fail the second one on whichever came first.
/// </para>
/// <para>
/// The thread is a background thread, so it does not keep the test run alive once the last test has
/// finished with it.
/// </para>
/// </remarks>
internal static class Wpf
{
    private static readonly Lazy<Dispatcher> Ui = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Dispatcher Start()
    {
        var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;

        var thread = new Thread(() =>
        {
            // Before the theme: constructing an Application is what registers the pack:// URI
            // scheme, and without it a component URI reports that its prefix is unrecognised.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/Pos.App;component/Theme.xaml", UriKind.RelativeOrAbsolute),
            });

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "wpf-tests",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        return dispatcher!;
    }

    /// <summary>Runs the body on the UI thread and rethrows anything it threw, in place.</summary>
    public static void Run(Action body) => Ui.Value.Invoke(body);

    /// <summary>
    /// Lays a window out at a given size without it appearing on screen.
    /// </summary>
    /// <remarks>
    /// Shown rather than measured in place. A <c>TabControl</c> does not build the visual tree for a
    /// tab until it is connected to a presentation source, so measuring an unshown window reports
    /// that every tab but the first is empty — and empty things fit anywhere. Moving it far off the
    /// desktop keeps it out of sight while it is laid out for real.
    /// </remarks>
    public static void LayOut(Window window, double width, double height)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Width = width;
        window.Height = height;
        window.Show();
        window.UpdateLayout();
    }

    /// <summary>Every visual descendant of a given type, in tree order.</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
                yield return match;

            foreach (var deeper in Descendants<T>(child))
                yield return deeper;
        }
    }
}
