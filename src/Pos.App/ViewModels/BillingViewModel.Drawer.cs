using System.Globalization;
using Pos.Core.Domain;
using Pos.Core.Hardware.Drawer;

namespace Pos.App.ViewModels;

/// <summary>Where a cash entry has got to on the till.</summary>
public enum DrawerStage
{
    /// <summary>What it is - float, expense, cash in or out - and how much.</summary>
    KindAndAmount = 0,

    /// <summary>For an expense: what it was for.</summary>
    Category = 1,

    /// <summary>For cash taken out: where it went.</summary>
    Note = 2,
}

public sealed partial class BillingViewModel
{
    /// <summary>Cash in and out of the drawer, or null on a lane wired without it.</summary>
    private readonly CashDrawerService? _cashDrawer;

    private DrawerStage _drawerStage;
    private int _selectedDrawerKindIndex;
    private int _selectedExpenseCategoryIndex;
    private decimal _drawerAmount;

    public bool IsUsingDrawer => _mode == BillingMode.Drawer;

    /// <summary>What can be entered, in the order the arrows walk them.</summary>
    public IReadOnlyList<DrawerEntryKind> DrawerKindChoices { get; } =
    [
        DrawerEntryKind.OpeningFloat,
        DrawerEntryKind.ExpenseFromDrawer,
        DrawerEntryKind.ExpenseFromOutside,
        DrawerEntryKind.CashIn,
        DrawerEntryKind.CashOut,
    ];

    public IReadOnlyList<string> DrawerKindLabels => [.. DrawerKindChoices.Select(k => k.Label())];

    public IReadOnlyList<string> ExpenseCategoryChoices => ExpenseCategories.All;

    public DrawerStage DrawerStage
    {
        get => _drawerStage;
        private set
        {
            if (!Set(ref _drawerStage, value))
                return;

            Raise(nameof(IsChoosingDrawerKind));
            Raise(nameof(IsChoosingExpenseCategory));
            Raise(nameof(DrawerPrompt));
            Raise(nameof(DrawerTitle));
        }
    }

    /// <summary>
    /// The pane's title, which follows the step: "Cash in and out" stayed up while it was asking what
    /// a 50-rupee expense had been for.
    /// </summary>
    public string DrawerTitle => _drawerStage switch
    {
        DrawerStage.Category => $"Expense of {Show.Money(_drawerAmount)}: what for?",
        DrawerStage.Note => $"{Show.Money(_drawerAmount)} taken out: where to?",
        _ => "Cash in and out",
    };

    public bool IsChoosingDrawerKind => _drawerStage == DrawerStage.KindAndAmount;

    public bool IsChoosingExpenseCategory => _drawerStage == DrawerStage.Category;

    public int SelectedDrawerKindIndex
    {
        get => _selectedDrawerKindIndex;
        private set
        {
            if (Set(ref _selectedDrawerKindIndex, value))
                Raise(nameof(DrawerPrompt));
        }
    }

    public int SelectedExpenseCategoryIndex
    {
        get => _selectedExpenseCategoryIndex;
        private set => Set(ref _selectedExpenseCategoryIndex, value);
    }

    private DrawerEntryKind SelectedDrawerKind =>
        DrawerKindChoices[Math.Clamp(_selectedDrawerKindIndex, 0, DrawerKindChoices.Count - 1)];

    /// <summary>What the box is asking for at this stage.</summary>
    public string DrawerPrompt => _drawerStage switch
    {
        DrawerStage.KindAndAmount => $"{SelectedDrawerKind.Label()}: the amount. Up and down to change what it is; {CommitKey} to go on.",
        DrawerStage.Category => $"Up and down to pick what it was for; type a note if you like; {CommitKey} to record.",
        _ => $"The bank, the owner - say where it is going. {CommitKey} to record.",
    };

    /// <summary>
    /// Opens the drawer pane: the float, an expense, cash put in or taken out.
    /// </summary>
    /// <remarks>
    /// Only with the bill empty. Cash moved in the middle of somebody's bill is cash nobody can
    /// later tell from their change.
    /// </remarks>
    public void CashDrawer()
    {
        ClearPendingConfirmations();
        CancelEdit();

        if (_cashDrawer is null)
        {
            StatusMessage = "Cash in and out is not available on this lane.";
            return;
        }

        if (Mode == BillingMode.Tender || !_bill.IsEmpty)
        {
            StatusMessage = "Finish, park or clear the bill first - cash in or out is not part of a sale.";
            return;
        }

        ResetDrawerEntry();

        // A day with no float yet is almost always a morning: offer the float first. Once it is in,
        // the commonest thing is an expense.
        var floatIn = SafeFloat();
        SelectedDrawerKindIndex = floatIn == 0m ? 0 : 1;

        Mode = BillingMode.Drawer;
        EditBuffer = string.Empty;

        StatusMessage = floatIn == 0m
            ? "No float recorded since the last close. Type the float counted into the drawer, or arrow down for an expense or cash in or out."
            : $"Float so far {Show.Money(floatIn)}. Type the amount; up and down for the float, an expense, or cash in or out.";
    }

    private void CommitDrawer()
    {
        if (_cashDrawer is null)
            return;

        switch (_drawerStage)
        {
            case DrawerStage.KindAndAmount:
            {
                var typed = EditBuffer.Trim();

                if (!TryParseAmount(typed, out var amount) || amount <= 0m)
                {
                    StatusMessage = typed.Length == 0 ? "Type the amount first." : $"'{typed}' is not an amount.";
                    return;
                }

                _drawerAmount = amount;
                var kind = SelectedDrawerKind;

                if (kind.IsExpense())
                {
                    SelectedExpenseCategoryIndex = 0;
                    DrawerStage = DrawerStage.Category;
                    EditBuffer = string.Empty;
                    StatusMessage = $"{Show.Money(amount)}: what was it for? Up and down to pick.";
                    return;
                }

                if (kind == DrawerEntryKind.CashOut)
                {
                    DrawerStage = DrawerStage.Note;
                    EditBuffer = string.Empty;
                    StatusMessage = $"{Show.Money(amount)} taken out: say where it is going.";
                    return;
                }

                RecordDrawer(new DrawerEntry(kind, amount));
                return;
            }

            case DrawerStage.Category:
            {
                var category = ExpenseCategories.All[Math.Clamp(_selectedExpenseCategoryIndex, 0, ExpenseCategories.All.Count - 1)];
                RecordDrawer(new DrawerEntry(SelectedDrawerKind, _drawerAmount, category, EditBuffer));
                return;
            }

            default:
                RecordDrawer(new DrawerEntry(DrawerEntryKind.CashOut, _drawerAmount, Note: EditBuffer));
                return;
        }
    }

    private void RecordDrawer(DrawerEntry entry)
    {
        DrawerEntryResult result;

        try
        {
            result = _cashDrawer!.Record(entry, _laneId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Left where it was, so the amount or the note can be put right rather than typed again.
            StatusMessage = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
            return;
        }

        var amount = Show.Money(entry.Amount);
        var message = entry.Kind switch
        {
            DrawerEntryKind.OpeningFloat => $"Float of {amount} recorded. {Show.Money(result.Recorded.FloatSinceClose)} in the drawer to start the day.",
            DrawerEntryKind.ExpenseFromDrawer => $"{entry.Category}: {amount} paid from the drawer.",
            DrawerEntryKind.ExpenseFromOutside => $"{entry.Category}: {amount} recorded, paid from outside the till. The drawer is not touched.",
            DrawerEntryKind.CashIn => $"{amount} put into the drawer.",
            _ => $"{amount} taken out of the drawer.",
        };

        if (result.Drawer == DrawerKickResult.Failed)
            message += " THE DRAWER DID NOT OPEN - use the key.";

        ResetDrawerEntry();
        Mode = BillingMode.Billing;
        EditBuffer = string.Empty;
        StatusMessage = message;
    }

    private void MoveInDrawer(int delta)
    {
        switch (_drawerStage)
        {
            case DrawerStage.KindAndAmount:
                SelectedDrawerKindIndex = Math.Clamp(_selectedDrawerKindIndex + delta, 0, DrawerKindChoices.Count - 1);
                return;

            case DrawerStage.Category:
                SelectedExpenseCategoryIndex = Math.Clamp(_selectedExpenseCategoryIndex + delta, 0, ExpenseCategories.All.Count - 1);
                return;
        }
    }

    /// <summary>Escape: back to the amount, and out from there. Nothing is recorded on the way.</summary>
    private void BackOutOfDrawer()
    {
        if (_drawerStage != DrawerStage.KindAndAmount)
        {
            DrawerStage = DrawerStage.KindAndAmount;
            EditBuffer = _drawerAmount.ToString("0.##", CultureInfo.InvariantCulture);
            StatusMessage = "Back to the amount.";
            return;
        }

        ResetDrawerEntry();
        Mode = BillingMode.Billing;
        EditBuffer = string.Empty;
        StatusMessage = "Nothing recorded.";
    }

    private void ResetDrawerEntry()
    {
        _drawerAmount = 0m;
        SelectedExpenseCategoryIndex = 0;
        DrawerStage = DrawerStage.KindAndAmount;
        Raise(nameof(DrawerPrompt));
        Raise(nameof(DrawerTitle));
    }

    /// <summary>The float so far, or nothing if it cannot be read - a message is not worth a failure.</summary>
    private decimal SafeFloat()
    {
        try
        {
            return _cashDrawer?.FloatSinceLastClose(_laneId) ?? 0m;
        }
        catch (Exception)
        {
            return 0m;
        }
    }
}
