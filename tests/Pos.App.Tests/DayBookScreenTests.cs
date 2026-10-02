using System.IO;
using Pos.App.ViewModels;
using Pos.Core.Analytics;
using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.TestSupport;
using Xunit;

namespace Pos.App.Tests;

/// <summary>
/// The day book from the owner's GST tab: the month on screen, written for the accountant, and
/// what the screen says about it.
/// </summary>
public class DayBookScreenTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    private readonly TempDatabase _temp = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pos-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _temp.Dispose();

        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private sealed class At(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment.ToUniversalTime();
    }

    private GstReturnViewModel Screen(Func<DateOnly, string, DayBookSaved>? dayBook = null, bool withDayBook = true)
    {
        var screen = new GstReturnViewModel(
            month => new GstReturnQuery(_temp.Database).Gather("L1", month, "33"),
            (_, _) => [],
            Today)
        {
            DayBook = withDayBook
                ? dayBook ?? ((month, path) =>
                {
                    var book = new DayBookQuery(_temp.Database).Month("L1", month, new DayBookLedgers());
                    return new DayBookSaved(book.Vouchers.Count, book.Notes, book.HasAnything ? DayBookFiles.Write(book, path) : []);
                })
                : null,
        };

        screen.Load();
        return screen;
    }

    private void SellInSeptember()
    {
        _temp.Items.UpsertRange([Catalogue.Item(sku: "DAL", name: "Toor Dal 1kg", price: 105m)]);

        var bill = new InvoiceEngine("33");
        bill.AddItem(_temp.Items.FindBySku("DAL")!);
        var basket = new TenderBasket(bill.Totals.AmountPayable);
        basket.Add(TenderType.Cash, 105m);

        new CheckoutService(new InvoiceRepository(_temp.Database), new CustomerRepository(_temp.Database), new RecordingDrawerService(),
            clock: new At(new DateTimeOffset(2026, 9, 20, 11, 0, 0, TimeSpan.FromHours(5.5)))).Complete("L1", bill, basket);
    }

    private string Path0 => Path.Combine(_folder, "daybook-L1-2026-09.csv");

    [Fact]
    public void LastMonthsDayBookIsWrittenAndSaidSo()
    {
        SellInSeptember();
        var screen = Screen();

        Assert.True(screen.CanSaveDayBook);
        Assert.Equal("daybook-L1-2026-09.csv", screen.SuggestedDayBookName);

        Assert.Null(screen.SaveDayBook(Path0));

        Assert.True(File.Exists(Path0));
        Assert.True(File.Exists(Path.Combine(_folder, "daybook-L1-2026-09-tally-vouchers.xml")));
        Assert.True(File.Exists(Path.Combine(_folder, "daybook-L1-2026-09-tally-ledgers.xml")));
        Assert.Equal($"Saved the day book for September 2026: 1 voucher, as a CSV and as 2 files for Tally, to {_folder}. They hold the shop's books - keep them private.", screen.Status);
    }

    [Fact]
    public void AMonthWithNothingInItWritesNothingAndSaysSo()
    {
        var screen = Screen();

        Assert.Equal("Nothing happened in September 2026 to put in a day book.", screen.SaveDayBook(Path0));
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void WhatDidNotAddUpIsSaidOnTheScreen()
    {
        var screen = Screen((_, _) => new DayBookSaved(3, ["Bill X did not balance by Rs 0.02; the difference is on Round off."], ["a.csv", "b.xml", "c.xml"]));

        Assert.Null(screen.SaveDayBook(Path0));
        Assert.EndsWith(" To look at: Bill X did not balance by Rs 0.02; the difference is on Round off.", screen.Status);
    }

    [Fact]
    public void AFailureIsSaidAndDoesNotTakeTheScreenDown()
    {
        var screen = Screen((_, _) => throw new IOException("the disk is full"));

        Assert.Equal("Could not save the day book: the disk is full", screen.SaveDayBook(Path0));
    }

    [Fact]
    public void AScreenBuiltWithoutOneOffersNone()
    {
        var screen = Screen(withDayBook: false);

        Assert.False(screen.CanSaveDayBook);
        Assert.Equal("This screen cannot save a day book.", screen.SaveDayBook(Path0));
    }
}
