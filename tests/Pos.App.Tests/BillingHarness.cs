using System.Windows.Input;
using Pos.App.Input;
using Pos.App.ViewModels;
using Pos.Core.Domain;
using Pos.TestSupport;

namespace Pos.App.Tests;

/// <summary>
/// A till driven entirely through the keyboard router, over a real database. Tests using this
/// never call a view model action directly — they press keys, which is the only way to prove the
/// flow is genuinely keyboard-reachable rather than merely keyboard-shaped.
/// </summary>
public sealed class BillingHarness : IDisposable
{
    public const double ScannerGapMs = 5;
    public const double HumanGapMs = 120;
    public const double DebounceMs = 150;

    private readonly TempDatabase _temp;

    public BillingHarness(params Item[] catalogue)
    {
        _temp = new TempDatabase();

        if (catalogue.Length > 0)
            _temp.Items.AddRange(catalogue);

        Clock = new FakeClock();
        Scheduler = new VirtualScheduler();

        ViewModel = new BillingViewModel(
            new InvoiceEngine(OutletStateCode),
            _temp.Items,
            Scheduler,
            Clock,
            TimeSpan.FromMilliseconds(DebounceMs),
            TimeSpan.FromMilliseconds(30));

        Router = new KeyboardRouter(Keymap.Default, ViewModel);
    }

    public const string OutletStateCode = "33";

    public FakeClock Clock { get; }

    public VirtualScheduler Scheduler { get; }

    public BillingViewModel ViewModel { get; }

    public KeyboardRouter Router { get; }

    /// <summary>Presses a key. Returns whether the default keymap had anything bound to it.</summary>
    public bool Press(Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        Router.Handle(key, modifiers);

    /// <summary>Types into the search box at the given pace, one character at a time.</summary>
    public void Type(string text, double gapMs)
    {
        foreach (var character in text)
        {
            Clock.Advance(gapMs);
            ViewModel.SearchText += character;
        }
    }

    /// <summary>A scanner burst: characters in a tight burst, terminated by Enter.</summary>
    public void Scan(string barcode)
    {
        Type(barcode, ScannerGapMs);
        Clock.Advance(ScannerGapMs);
        Press(Key.Enter);
    }

    /// <summary>A person typing, then waiting long enough for the debounced query to run.</summary>
    public void TypeAndWait(string text)
    {
        Type(text, HumanGapMs);
        Scheduler.Advance(DebounceMs);
    }

    public void Dispose()
    {
        ViewModel.Dispose();
        _temp.Dispose();
    }
}
